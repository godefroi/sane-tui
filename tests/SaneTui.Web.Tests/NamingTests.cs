using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using SaneTui.Sane;

namespace SaneTui.Web.Tests;

public class NamingTests
{
	private const string ModelAnswer = """{"Title":"Electric bill from Pacific Power","DocumentDate":"2024-03-01"}""";

	private sealed class FakeTesseract(string text) : Tesseract(Options.Create(new NamingOptions()))
	{
		public override Task<string> ReadAsync(IReadOnlyList<string> pageFiles, CancellationToken cancellationToken) => Task.FromResult(text);
	}

	// stands in for the Azure OpenAI service: records what it was asked and gives a chat completion with the given content
	private sealed class FakeService(string content) : HttpMessageHandler
	{
		public string? Url { get; private set; }

		public string? Authorization { get; private set; }

		public string? Body { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Url = request.RequestUri!.ToString();
			Authorization = request.Headers.Authorization?.ToString();
			Body = await request.Content!.ReadAsStringAsync(cancellationToken);

			var completion = JsonSerializer.Serialize(new {
				id = "chatcmpl-test",
				@object = "chat.completion",
				created = 1,
				model = "test",
				choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
				usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 },
			});

			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion, Encoding.UTF8, "application/json") };
		}
	}

	private static IChatClient ClientFor(FakeService service) =>
		new OpenAIClient(new System.ClientModel.ApiKeyCredential("the-key"), new OpenAIClientOptions { Endpoint = new Uri("https://example.test/openai/v1/"), Transport = new HttpClientPipelineTransport(new HttpClient(service)) })
			.GetChatClient("the-model")
			.AsIChatClient();

	private static NamingOptions Configured() => new() { Endpoint = new Uri("https://example.test/"), ApiKey = "the-key", Model = "the-model" };

	private static void Configure(NamingOptions o)
	{
		o.Endpoint = new Uri("https://example.test/");
		o.ApiKey = "k";
		o.Model = "d";
	}

	private static void AddAPage(AppFactory app) =>
		app.Services.GetRequiredService<DocumentStore>().AddPage(new SaneImage(8, 8, SanePixelFormat.Gray, 8, 8, new byte[64]), 100, default);

	[Test]
	[Arguments("Electric bill", "Electric bill")]
	[Arguments("  Lots   of\tspace\n here ", "Lots of space here")]
	[Arguments("../../etc/passwd", "etc passwd")]
	[Arguments("a/b\\c:d*e?f\"g<h>i|j", "a b c d e f g h i j")]
	[Arguments("bell\u0007and\u0000nul", "bell and nul")]
	[Arguments("ends with dots...", "ends with dots")]
	[Arguments("...", "")]
	[Arguments("", "")]
	[Arguments(null, "")]
	public async Task ATitle_IsMadeSafeForAFileName(string? title, string expected)
	{
		await Assert.That(DocumentNames.CleanTitle(title)).IsEqualTo(expected);
	}

	[Test]
	public async Task ALongTitle_IsCut()
	{
		await Assert.That(DocumentNames.CleanTitle(new string('x', 300)).Length).IsEqualTo(100);
	}

	[Test]
	public async Task ThePdfName_IsTheDateAndTitle_OrATimeStampWithNoTitle()
	{
		var now = new DateTime(2026, 10, 17, 14, 5, 9);

		await Assert.That(DocumentNames.PdfFileName("Electric bill", new DateOnly(2024, 3, 1), now)).IsEqualTo("2024-03-01 Electric bill.pdf");
		await Assert.That(DocumentNames.PdfFileName("Electric bill", null, now)).IsEqualTo("Electric bill.pdf");
		await Assert.That(DocumentNames.PdfFileName("", new DateOnly(2024, 3, 1), now)).IsEqualTo("scan-20261017-140509.pdf");
		await Assert.That(DocumentNames.PdfFileName("../../x", null, now)).IsEqualTo("x.pdf");
	}
	[Test]
	public async Task AnAnswer_IsReadForItsTitleAndFullDate()
	{
		var suggestion = NameSuggester.Interpret("Electric bill from Pacific Power", "2024-03-01");

		await Assert.That(suggestion.Title).IsEqualTo("Electric bill from Pacific Power");
		await Assert.That(suggestion.Date).IsEqualTo(new DateOnly(2024, 3, 1));
	}

	[Test]
	[Arguments("2024-03")]
	[Arguments("2024-13-45")]
	[Arguments("March 1st")]
	[Arguments("")]
	[Arguments(null)]
	public async Task ADateThatIsNotAFullRealDate_IsLeftOut(string? date)
	{
		var suggestion = NameSuggester.Interpret("x", date);

		await Assert.That(suggestion.Title).IsEqualTo("x");
		await Assert.That(suggestion.Date).IsNull();
	}

	[Test]
	public async Task AnUnusableTitle_BecomesAnEmptyOne()
	{
		await Assert.That(NameSuggester.Interpret(null, "2024-03-01").Title).IsEqualTo("");
		await Assert.That(NameSuggester.Interpret("../..", null).Title).IsEqualTo("");
	}

	[Test]
	public async Task AnAnswerThatIsNotInTheFormAskedFor_IsReportedNotCrashedOn()
	{
		var service = new FakeService("this is not json");
		var suggester = new NameSuggester(Options.Create(Configured()), new FakeTesseract("some text"), ClientFor(service));

		await Assert.ThrowsAsync<NamingException>(async () => await suggester.SuggestAsync(["page.jpg"], CancellationToken.None));
	}

	[Test]
	[Arguments("https://example.openai.azure.com/", "https://example.openai.azure.com/openai/v1/")]
	[Arguments("https://example.openai.azure.com", "https://example.openai.azure.com/openai/v1/")]
	[Arguments("https://example.openai.azure.com/openai/v1/", "https://example.openai.azure.com/openai/v1/")]
	[Arguments("https://example.services.ai.azure.com/openai/v1", "https://example.services.ai.azure.com/openai/v1")]
	[Arguments("http://localhost:18999/", "http://localhost:18999/openai/v1/")]
	public async Task TheEndpoint_GetsTheOpenAIPathIfItWasLeftOff(string given, string expected)
	{
		await Assert.That(new NamingOptions { Endpoint = new Uri(given) }.BaseUri!.ToString()).IsEqualTo(expected);
	}

	[Test]
	public async Task TheModelIsGivenThePagesText_AndAnswersFromIt()
	{
		var service = new FakeService(ModelAnswer);
		var suggester = new NameSuggester(Options.Create(Configured()), new FakeTesseract("PACIFIC POWER statement 03/01/2024 amount due $52.10"), ClientFor(service));

		var suggestion = await suggester.SuggestAsync(["page.jpg"], CancellationToken.None);

		await Assert.That(suggestion).IsEqualTo(new NameSuggestion("Electric bill from Pacific Power", new DateOnly(2024, 3, 1)));
		await Assert.That(service.Url).IsEqualTo("https://example.test/openai/v1/chat/completions");
		await Assert.That(service.Authorization).IsEqualTo("Bearer the-key");
		await Assert.That(service.Body).Contains("PACIFIC POWER statement");
		await Assert.That(service.Body).Contains("\"model\":\"the-model\"");
		await Assert.That(service.Body).Contains("json_schema");
		await Assert.That(service.Body).Contains("documentDate"); // the schema comes from the answer's type
	}

	[Test]
	public async Task WithNoTextOnThePages_TheModelIsNotAsked()
	{
		var service = new FakeService(ModelAnswer);
		var suggester = new NameSuggester(Options.Create(Configured()), new FakeTesseract("  \n "), ClientFor(service));

		var suggestion = await suggester.SuggestAsync(["page.jpg"], CancellationToken.None);

		await Assert.That(suggestion.Title).IsEqualTo("");
		await Assert.That(service.Body).IsNull();
	}

	[Test]
	public async Task WithoutSettings_SuggestingSaysSoInsteadOfFailingObscurely()
	{
		var suggester = new NameSuggester(Options.Create(new NamingOptions()), new FakeTesseract("text"));

		var error = await Assert.ThrowsAsync<NamingException>(async () => await suggester.SuggestAsync(["page.jpg"], CancellationToken.None));

		await Assert.That(error!.Message).Contains("Naming__Endpoint");
	}

	[Test]
	public async Task TheService_OffersNamingOnlyWhenItIsSetUp()
	{
		using var off = new AppFactory();
		using var on = new AppFactory(configureServices: s => s.Configure<NamingOptions>(Configure));
		using var offClient = off.SignedIn();
		using var onClient = on.SignedIn();

		await Assert.That(await offClient.GetStringAsync("/api/config")).IsEqualTo("""{"naming":false,"retentionDays":30}""");
		await Assert.That(await onClient.GetStringAsync("/api/config")).IsEqualTo("""{"naming":true,"retentionDays":30}""");
	}

	[Test]
	public async Task TheNameEndpoint_NeedsNamingToBeSetUp_AndAPage()
	{
		using var off = new AppFactory();
		using var offClient = off.SignedIn();

		await Assert.That((await offClient.PostAsync("/api/document/name", null)).StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

		var service = new FakeService(ModelAnswer);

		using var on = new AppFactory(configureServices: s => {
			s.Configure<NamingOptions>(Configure);
			s.AddSingleton(ClientFor(service));
			s.AddSingleton<Tesseract>(new FakeTesseract("PACIFIC POWER"));
		});

		using var client = on.SignedIn();

		await Assert.That((await client.PostAsync("/api/document/name", null)).StatusCode).IsEqualTo(HttpStatusCode.NotFound); // no document yet

		AddAPage(on);

		var response = await client.PostAsync("/api/document/name", null);
		var answer = await response.Content.ReadFromJsonAsync<JsonElement>();

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
		await Assert.That(answer.GetProperty("title").GetString()).IsEqualTo("Electric bill from Pacific Power");
		await Assert.That(answer.GetProperty("date").GetString()).IsEqualTo("2024-03-01");
	}

	[Test]
	public async Task TheDownloadedPdf_IsNamedFromTheTitleAndDate()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();

		AddAPage(app);

		var named = await client.GetAsync("/api/document.pdf?title=Electric%20bill&date=2024-03-01");
		var hostile = await client.GetAsync("/api/document.pdf?title=..%2F..%2Fetc%2Fpasswd&date=not-a-date");

		await Assert.That(named.Content.Headers.ContentDisposition!.FileName).IsEqualTo("2024-03-01 Electric bill.pdf");
		await Assert.That(hostile.Content.Headers.ContentDisposition!.FileName).IsEqualTo("etc passwd.pdf");
	}

	[Test]
	public async Task Tesseract_ReadsTheTextOfAPicture()
	{
		var tesseract = new Tesseract(Options.Create(new NamingOptions()));

		using var dir = new TempFolder();
		var path = Path.Combine(dir.Path, "page.png");

		using (var page = new MagickImage(MagickColors.White, 1200, 400)) {
			new Drawables().Font("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf").FontPointSize(96).FillColor(MagickColors.Black).Text(60, 220, "ELECTRIC BILL").Draw(page);
			page.Density = new Density(300);
			page.Write(path);
		}

		try {
			var text = await tesseract.ReadAsync([path], CancellationToken.None);

			await Assert.That(text.ToUpperInvariant()).Contains("ELECTRIC");
		} catch (NamingException ex) when (ex.InnerException is System.ComponentModel.Win32Exception) {
			Skip.Test("tesseract isn't installed here; run these in the container (see tests/Dockerfile).");
		}
	}

	[Test]
	public async Task Tesseract_RefusesLanguageCodesThatAreNotCodes()
	{
		var tesseract = new Tesseract(Options.Create(new NamingOptions { Languages = "eng --oem 0" }));

		await Assert.ThrowsAsync<NamingException>(async () => await tesseract.ReadAsync(["x.png"], CancellationToken.None));
	}
}
