using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SaneTui.Sane;

namespace SaneTui.Web.Tests;

public class PaperlessServiceTests
{
	private sealed class FakePaperless : HttpMessageHandler
	{
		public List<string> Uploads { get; } = [];

		public Queue<(HttpStatusCode, string)> Tasks { get; } = new();

		public (HttpStatusCode, string) LastTask { get; set; } = (HttpStatusCode.OK, """{"results":[{"status":"started"}]}""");

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var path = request.RequestUri!.AbsolutePath;
			(HttpStatusCode, string) answer;

			if (path == "/api/documents/post_document/") {
				Uploads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
				answer = (HttpStatusCode.OK, "\"task-1\"");
			} else if (path == "/api/tasks/") {
				answer = Tasks.Count > 0 ? Tasks.Dequeue() : LastTask;
			} else {
				var kind = path.Contains("correspondents") ? (7, "Pacific Power") : (3, "Statement");

				answer = (HttpStatusCode.OK, JsonSerializer.Serialize(new { next = (string?)null, results = new[] { new { id = kind.Item1, name = kind.Item2 } } }));
			}

			return new HttpResponseMessage(answer.Item1) { Content = new StringContent(answer.Item2, Encoding.UTF8, "application/json") };
		}
	}

	private sealed class Lines : ILoggerFactory, ILogger
	{
		public List<string> Written { get; } = [];

		public void AddProvider(ILoggerProvider provider) { }

		public ILogger CreateLogger(string categoryName) => categoryName == SuggestionLog.Category ? this : NullLogger.Instance;

		public void Dispose() { }

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Written.Add(formatter(state, exception));
	}

	private sealed class Rig : IAsyncDisposable
	{
		private readonly TempFolder _folder = new();

		public FakePaperless Paperless { get; } = new();

		public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

		public Lines Log { get; } = new();

		public DocumentStore Store { get; private set; } = null!;

		public ScannerService Scanner { get; private set; } = null!;

		public PaperlessService Service { get; private set; } = null!;

		public Rig(bool page = true)
		{
			Start();

			if (page) {
				AddPage();
			}
		}

		public void AddPage() => Store.AddPage(new SaneImage(16, 16, SanePixelFormat.Gray, 8, 16, new byte[256]), 100, default);

		// a new store and service on the same folder, as after a restart
		public void Start()
		{
			var http = new HttpClient(Paperless) { BaseAddress = new Uri("http://paperless.test/") };

			PaperlessClient.Configure(http, "t");

			Store = new DocumentStore(Options.Create(new WebOptions { DataFolder = _folder.Path }));
			Scanner = new ScannerService(Store);
			Service = new PaperlessService(new PaperlessClient(http, Time), Store, Scanner, new SuggestionLog(Log, Time), Options.Create(new PaperlessOptions { Url = new Uri("http://paperless.test/"), Token = "t" }), Time, NullLogger<PaperlessService>.Instance);
		}

		// lets the poll's clock run until the document is no longer "consuming"
		public async Task<SendInfo?> Settled()
		{
			for (var i = 0; i < 400; i++) {
				if (Store.Current?.Sent is { Status: not "consuming" } sent) {
					return sent;
				}

				Time.Advance(TimeSpan.FromSeconds(2));
				await Task.Delay(5);
			}

			return Store.Current?.Sent;
		}

		public async ValueTask DisposeAsync()
		{
			await Service.StopAsync(CancellationToken.None);
			_folder.Dispose();
		}
	}

	private static SendRequest Plain => new("Electric bill", "2024-03-01", 7, 3);

	[Test]
	public async Task ASend_UploadsWithWhatWasAsked_AndFollowsTheTaskToTheDocument()
	{
		await using var rig = new Rig();

		rig.Paperless.Tasks.Enqueue((HttpStatusCode.OK, """{"results":[{"status":"pending"}]}"""));
		rig.Paperless.LastTask = (HttpStatusCode.OK, """{"results":[{"status":"success","result_data":{"document_id":41}}]}""");

		var sent = await rig.Service.SendAsync(Plain, default);

		await Assert.That(sent.Status).IsEqualTo("consuming");
		await Assert.That(sent.TaskId).IsEqualTo("task-1");
		await Assert.That(rig.Paperless.Uploads.Single()).Contains("filename=\"2024-03-01 Electric bill.pdf\"");
		await Assert.That(rig.Paperless.Uploads.Single()).Contains("name=correspondent\r\n\r\n7");

		var settled = await rig.Settled();

		await Assert.That(settled!.Status).IsEqualTo("done");
		await Assert.That(settled.DocumentId).IsEqualTo(41);

		// it kept the document: sending doesn't discard it
		await Assert.That(rig.Store.Current!.Pages.Count).IsEqualTo(1);
	}

	[Test]
	public async Task TheLog_SaysWhatWasSent_AndHowItWentLater_AsOneJsonObjectEach()
	{
		await using var rig = new Rig();

		rig.Paperless.LastTask = (HttpStatusCode.OK, """{"results":[{"status":"success","result_data":{"document_id":41}}]}""");

		await rig.Service.SendAsync(Plain, default);
		await rig.Settled();

		var entries = rig.Log.Written.Select(l => JsonDocument.Parse(l).RootElement).ToList();

		await Assert.That(entries.Select(e => e.GetProperty("event").GetString()!)).IsEquivalentTo(["sent", "consumed"]);
		await Assert.That(entries[0].GetProperty("correspondent").GetProperty("name").GetString()).IsEqualTo("Pacific Power");
		await Assert.That(entries[0].GetProperty("documentType").GetProperty("name").GetString()).IsEqualTo("Statement");
		await Assert.That(entries[0].GetProperty("at").GetString()).StartsWith("2026-10-01T12:00:00");
		await Assert.That(entries[1].GetProperty("status").GetString()).IsEqualTo("done");
	}

	[Test]
	public async Task AFailedTask_ShowsWhy_AndAFailedDocumentMaySimplyBeSentAgain()
	{
		await using var rig = new Rig();

		rig.Paperless.LastTask = (HttpStatusCode.OK, """{"results":[{"status":"failure","result_data":{"error_message":"bad pdf"}}]}""");

		await rig.Service.SendAsync(Plain, default);

		var settled = await rig.Settled();

		await Assert.That(settled!.Status).IsEqualTo("failed");
		await Assert.That(settled.Message).IsEqualTo("bad pdf");

		await rig.Service.SendAsync(Plain, default); // no Again needed

		await Assert.That(rig.Paperless.Uploads.Count).IsEqualTo(2);
	}

	[Test]
	public async Task WhileItIsWithPaperless_ASecondSendIsRefused()
	{
		await using var rig = new Rig();

		await rig.Service.SendAsync(Plain, default);

		await Assert.That(async () => { await rig.Service.SendAsync(Plain with { Again = true }, default); }).Throws<ConflictException>();
		await Assert.That(rig.Paperless.Uploads.Count).IsEqualTo(1);
	}

	[Test]
	public async Task ADocumentThatIsDone_NeedsAgain_ToBeSentASecondTime()
	{
		await using var rig = new Rig();

		rig.Paperless.LastTask = (HttpStatusCode.OK, """{"results":[{"status":"success","result_data":{"document_id":41}}]}""");

		await rig.Service.SendAsync(Plain, default);
		await rig.Settled();

		await Assert.That(async () => { await rig.Service.SendAsync(Plain, default); }).Throws<ConflictException>();

		await rig.Service.SendAsync(Plain with { Again = true }, default);

		await Assert.That(rig.Paperless.Uploads.Count).IsEqualTo(2);
	}

	[Test]
	public async Task AnIdThePageInvented_IsRefusedBeforeAnythingIsUploaded()
	{
		await using var rig = new Rig();

		await Assert.That(async () => { await rig.Service.SendAsync(Plain with { CorrespondentId = 999 }, default); }).Throws<CommandException>();
		await Assert.That(async () => { await rig.Service.SendAsync(Plain with { DocumentTypeId = 999 }, default); }).Throws<CommandException>();
		await Assert.That(rig.Paperless.Uploads).IsEmpty();
	}

	[Test]
	[Arguments("03/01/2024")]
	[Arguments("2024-13-40")]
	[Arguments("soon")]
	public async Task ADateThatIsNotOne_IsRefused(string date)
	{
		await using var rig = new Rig();

		await Assert.That(async () => { await rig.Service.SendAsync(Plain with { Date = date }, default); }).Throws<CommandException>();
	}

	[Test]
	public async Task NothingToSend_IsRefused()
	{
		await using var rig = new Rig(page: false);

		await Assert.That(async () => { await rig.Service.SendAsync(Plain, default); }).Throws<CommandException>();
	}

	[Test]
	public async Task NoTitle_NoDate_NoChoices_IsStillASend()
	{
		await using var rig = new Rig();

		await rig.Service.SendAsync(new SendRequest(null, null, null, null), default);

		await Assert.That(rig.Paperless.Uploads.Single()).DoesNotContain("name=title");
	}

	[Test]
	public async Task PaperlessGoingQuiet_EndsAsUnknown_NotAsAnEndlessSpinner()
	{
		await using var rig = new Rig();

		rig.Paperless.LastTask = (HttpStatusCode.InternalServerError, "down");

		await rig.Service.SendAsync(Plain, default);

		var settled = await rig.Settled();

		await Assert.That(settled!.Status).IsEqualTo("unknown");
		await Assert.That(settled.Message).Contains("500");
	}

	[Test]
	public async Task ATaskThatNeverFinishes_IsGivenUpOn_AfterAWhile()
	{
		await using var rig = new Rig();

		await rig.Service.SendAsync(Plain, default);
		rig.Time.Advance(TimeSpan.FromMinutes(16));

		var settled = await rig.Settled();

		await Assert.That(settled!.Status).IsEqualTo("unknown");
		await Assert.That(settled.Message).Contains("15 minutes");
	}

	[Test]
	public async Task ChangingThePages_ForgetsThatItWasSent_BecauseItIsADifferentDocumentNow()
	{
		await using var rig = new Rig();

		rig.Paperless.LastTask = (HttpStatusCode.OK, """{"results":[{"status":"success","result_data":{"document_id":41}}]}""");

		await rig.Service.SendAsync(Plain, default);
		await rig.Settled();
		rig.AddPage();

		await Assert.That(rig.Store.Current!.Sent).IsNull();

		await rig.Service.SendAsync(Plain, default); // no Again needed
	}

	[Test]
	public async Task WhereItStands_SurvivesARestart_AndTheNewServiceCarriesOnAsking()
	{
		await using var rig = new Rig();

		await rig.Service.SendAsync(Plain, default);
		await rig.Service.StopAsync(default);

		rig.Start(); // same folder, new objects

		await Assert.That(rig.Store.Current!.Sent!.Status).IsEqualTo("consuming");

		rig.Paperless.LastTask = (HttpStatusCode.OK, """{"results":[{"status":"success","result_data":{"document_id":8}}]}""");
		await rig.Service.StartAsync(default);

		var settled = await rig.Settled();

		await Assert.That(settled!.Status).IsEqualTo("done");
		await Assert.That(settled.DocumentId).IsEqualTo(8);
	}
}
