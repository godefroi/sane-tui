using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace SaneTui.Web.Tests;

public class PaperlessClientTests
{
	private sealed record Seen(HttpMethod Method, string Url, string? Authorization, string? Accept, string? Body);

	private sealed class Stub(Func<Seen, (HttpStatusCode, string)> answer) : HttpMessageHandler
	{
		public List<Seen> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var seen = new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), string.Join(",", request.Headers.Accept), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

			Requests.Add(seen);

			var (status, body) = answer(seen);

			return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}

	private static (PaperlessClient Client, Stub Stub, FakeTimeProvider Time) Make(Func<Seen, (HttpStatusCode, string)> answer)
	{
		var stub = new Stub(answer);
		var http = new HttpClient(stub) { BaseAddress = new Uri("http://paperless.test/") };

		PaperlessClient.Configure(http, "secret");

		var time = new FakeTimeProvider();

		return (new PaperlessClient(http, time), stub, time);
	}

	private static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

	private static string Page(bool more, params (int Id, string Name)[] items) =>
		JsonSerializer.Serialize(new { count = items.Length, next = more ? "http://wrong.host/next" : null, results = items.Select(i => new { id = i.Id, name = i.Name, slug = "x" }) });

	[Test]
	public async Task RequestsCarryTheToken_AndAskForApiVersion10()
	{
		var (client, stub, _) = Make(_ => Ok(Page(false, (1, "A"))));

		await client.GetChoicesAsync(CancellationToken.None);

		await Assert.That(stub.Requests.All(r => r.Authorization == "Token secret")).IsTrue();
		await Assert.That(stub.Requests[0].Accept).Contains("version=10");
	}

	[Test]
	public async Task ChoicesAreReadPageByPage_ByNumber_NotByTheAddressTheServerGives()
	{
		var (client, stub, _) = Make(r =>
			r.Url.StartsWith("/api/correspondents/") ? Ok(r.Url.EndsWith("page=1") ? Page(true, (1, "A")) : Page(false, (2, "B")))
			: Ok(Page(false, (5, "Invoice"))));

		var choices = await client.GetChoicesAsync(CancellationToken.None);

		await Assert.That(choices.Correspondents.Select(c => c.Name)).IsEquivalentTo(["A", "B"]);
		await Assert.That(choices.DocumentTypes.Single().Id).IsEqualTo(5);
		await Assert.That(stub.Requests.Select(r => r.Url)).IsEquivalentTo([
			"/api/correspondents/?page_size=1000&page=1",
			"/api/correspondents/?page_size=1000&page=2",
			"/api/document_types/?page_size=1000&page=1",
		]);
	}

	[Test]
	public async Task ChoicesAreKeptForAWhile_ThenAskedForAgain()
	{
		var (client, stub, time) = Make(_ => Ok(Page(false, (1, "A"))));

		await client.GetChoicesAsync(CancellationToken.None);
		await client.GetChoicesAsync(CancellationToken.None);

		await Assert.That(stub.Requests.Count).IsEqualTo(2); // one request per list, once

		time.Advance(TimeSpan.FromMinutes(6));
		await client.GetChoicesAsync(CancellationToken.None);

		await Assert.That(stub.Requests.Count).IsEqualTo(4);
	}

	[Test]
	[Arguments(HttpStatusCode.Unauthorized, "Paperless__Token")]
	[Arguments(HttpStatusCode.Forbidden, "Paperless__Token")]
	[Arguments(HttpStatusCode.NotFound, "Paperless__Url")]
	[Arguments(HttpStatusCode.InternalServerError, "500")]
	public async Task AFailure_SaysWhatToLookAt(HttpStatusCode status, string mention)
	{
		var (client, _, _) = Make(_ => (status, "oops"));

		var ex = await Assert.That(async () => { await client.GetChoicesAsync(CancellationToken.None); }).Throws<PaperlessException>();

		await Assert.That(ex!.Message).Contains(mention);
	}

	[Test]
	public async Task AnAnswerThatIsNotAList_PointsAtTheUrlSetting()
	{
		var (client, _, _) = Make(_ => Ok("<html>login</html>"));

		var ex = await Assert.That(async () => { await client.GetChoicesAsync(CancellationToken.None); }).Throws<PaperlessException>();

		await Assert.That(ex!.Message).Contains("Paperless__Url");
	}

	[Test]
	public async Task AnUnreachableServer_IsAPaperlessException()
	{
		var http = new HttpClient(new Refusing()) { BaseAddress = new Uri("http://paperless.test/") };
		var client = new PaperlessClient(http, TimeProvider.System);

		await Assert.That(async () => { await client.GetChoicesAsync(CancellationToken.None); }).Throws<PaperlessException>();
	}

	private sealed class Refusing : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("Connection refused");
	}

	[Test]
	public async Task Upload_SendsTheFileAndOnlyTheFieldsThatAreKnown_AndReturnsTheTaskId()
	{
		var (client, stub, _) = Make(_ => Ok("\"3f2a-task\""));

		var id = await client.UploadAsync(new MemoryStream("%PDF-1.4"u8.ToArray()), "2024-03-01 Electric.pdf", new PaperlessMetadata("Electric bill", new DateOnly(2024, 3, 1), 7, 3), CancellationToken.None);

		await Assert.That(id).IsEqualTo("3f2a-task");

		var sent = stub.Requests.Single();

		await Assert.That(sent.Method).IsEqualTo(HttpMethod.Post);
		await Assert.That(sent.Url).IsEqualTo("/api/documents/post_document/");
		await Assert.That(sent.Body).Contains("name=document; filename=\"2024-03-01 Electric.pdf\"");
		await Assert.That(sent.Body).Contains("Content-Type: application/pdf");
		await Assert.That(sent.Body).Contains("%PDF-1.4");

		foreach (var (field, value) in new[] { ("title", "Electric bill"), ("created", "2024-03-01"), ("correspondent", "7"), ("document_type", "3") }) {
			await Assert.That(sent.Body).Contains($"name={field}\r\n\r\n{value}");
		}
	}

	[Test]
	public async Task Upload_LeavesOutWhatIsNotKnown()
	{
		var (client, stub, _) = Make(_ => Ok("\"t\""));

		await client.UploadAsync(new MemoryStream([1]), "x.pdf", new PaperlessMetadata(null, null, null, null), CancellationToken.None);

		var body = stub.Requests.Single().Body!;

		foreach (var field in new[] { "title", "created", "correspondent", "document_type" }) {
			await Assert.That(body).DoesNotContain($"name={field}");
		}
	}

	[Test]
	public async Task Upload_AnAnswerThatIsNotATaskId_IsReported()
	{
		var (client, _, _) = Make(_ => Ok("{\"detail\":\"hm\"}"));

		await Assert.That(async () => { await client.UploadAsync(new MemoryStream([1]), "x.pdf", new PaperlessMetadata(null, null, null, null), CancellationToken.None); }).Throws<PaperlessException>();
	}

	[Test]
	[Arguments("""{"results":[]}""", "Waiting")]
	[Arguments("""[]""", "Waiting")]
	[Arguments("""{"results":[{"status":"pending"}]}""", "Waiting")]
	[Arguments("""{"results":[{"status":"started"}]}""", "Waiting")]
	[Arguments("""{"results":[{"status":"success","result_data":{"document_id":41}}]}""", "Done")]
	[Arguments("""[{"status":"success","result_data":{"document_id":41}}]""", "Done")]
	[Arguments("""{"results":[{"status":"failure","result_data":{"error_message":"bad pdf"}}]}""", "Failed")]
	[Arguments("""{"results":[{"status":"revoked"}]}""", "Failed")]
	[Arguments("""{"results":[{"status":"success","result_data":{"duplicate_of":12}}]}""", "Failed")]
	[Arguments("""{"results":[{"status":"something-new"}]}""", "Waiting")]
	public async Task ATaskIsReadFromEitherShapeOfAnswer(string answer, string expected)
	{
		var (client, stub, _) = Make(_ => Ok(answer));

		var result = await client.GetTaskAsync("abc 1", CancellationToken.None);

		await Assert.That(result.State).IsEqualTo(Enum.Parse<PaperlessTaskState>(expected));
		await Assert.That(stub.Requests.Single().Url).IsEqualTo("/api/tasks/?task_id=abc%201");
	}

	[Test]
	public async Task ATaskThatIsDone_NamesTheDocument_AndAFailedOneSaysWhy()
	{
		var (client, _, _) = Make(_ => Ok("""{"results":[{"status":"success","result_data":{"document_id":41}}]}"""));

		await Assert.That((await client.GetTaskAsync("t", CancellationToken.None)).DocumentId).IsEqualTo(41);

		(client, _, _) = Make(_ => Ok("""{"results":[{"status":"success","result_data":{"duplicate_of":12}}]}"""));

		await Assert.That((await client.GetTaskAsync("t", CancellationToken.None)).Message).Contains("#12");

		(client, _, _) = Make(_ => Ok("""{"results":[{"status":"failure","result_data":{"error_message":"bad pdf"}}]}"""));

		await Assert.That((await client.GetTaskAsync("t", CancellationToken.None)).Message).IsEqualTo("bad pdf");
	}

	[Test]
	public async Task TheOptionsAreAvailableOnlyWithBothAUrlAndAToken_AndAddressesGetATrailingSlash()
	{
		await Assert.That(new PaperlessOptions().Available).IsFalse();
		await Assert.That(new PaperlessOptions { Url = new Uri("http://p") }.Available).IsFalse();
		await Assert.That(new PaperlessOptions { Token = "t" }.Available).IsFalse();

		var options = new PaperlessOptions { Url = new Uri("http://p:8000/paperless"), Token = "t" };

		await Assert.That(options.Available).IsTrue();
		await Assert.That(options.BaseUri!.AbsoluteUri).IsEqualTo("http://p:8000/paperless/");
		await Assert.That(options.LinkBase!.AbsoluteUri).IsEqualTo("http://p:8000/paperless/");

		options.PublicUrl = new Uri("https://docs.example/");

		await Assert.That(options.LinkBase!.AbsoluteUri).IsEqualTo("https://docs.example/");
	}
}
