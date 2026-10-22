using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace SaneTui.Web.Tests;

// A wrong correspondent is worse than none, so one is suggested only when the model is certain, names one that is on Paperless's
// list, and quotes words that are really in the document. These tests are that gate.
public class SuggestionGateTests
{
	private const string Text = "PACIFIC POWER\nStatement date 03/01/2024\nAmount due $52.10\nPlease pay by 03/20/2024.";

	private static readonly PaperlessChoice[] Correspondents = [new(7, "Pacific Power"), new(9, "Acme Insurance")];
	private static readonly PaperlessChoice[] DocumentTypes = [new(2, "Invoice"), new(3, "Statement")];

	private static IReadOnlyDictionary<string, PaperlessChoice> Offered(IEnumerable<PaperlessChoice> choices) => choices.ToDictionary(c => c.Name.ToLowerInvariant());

	private static NameSuggestion Read(string answer, bool lists = true) =>
		NameSuggester.Interpret(answer, Text, lists ? Offered(Correspondents) : null, lists ? Offered(DocumentTypes) : null);

	private static string Answer(string? correspondent = "Pacific Power", string confidence = "certain", string? quote = "PACIFIC POWER", string? documentType = "Statement", string typeConfidence = "certain") =>
		JsonSerializer.Serialize(new {
			title = "Electric bill",
			documentDate = "2024-03-01",
			correspondent,
			correspondentQuote = quote,
			correspondentConfidence = confidence,
			documentType,
			documentTypeConfidence = typeConfidence,
		});

	[Test]
	public async Task ACertainChoice_OnTheList_WithAQuoteFromTheDocument_IsSuggested()
	{
		var suggestion = Read(Answer());

		await Assert.That(suggestion.Correspondent).IsEqualTo(new SuggestedChoice(7, "Pacific Power", "PACIFIC POWER"));
		await Assert.That(suggestion.DocumentType).IsEqualTo(new SuggestedChoice(3, "Statement", null));
		await Assert.That(suggestion.CorrespondentTrace!.Outcome).IsEqualTo("accepted");
	}

	[Test]
	public async Task TheNameIsMatchedWithoutRegardForCase_AndComesBackAsPaperlessSpellsIt()
	{
		var suggestion = Read(Answer(correspondent: "PACIFIC power", documentType: "invoice"));

		await Assert.That(suggestion.Correspondent!.Name).IsEqualTo("Pacific Power");
		await Assert.That(suggestion.DocumentType!.Name).IsEqualTo("Invoice");
	}

	[Test]
	public async Task AQuoteThatTheTextWrapsOrShoutsDifferently_StillCounts()
	{
		// the text has "STATEMENT DATE" on its own line, in capitals; the model copied it as a sentence
		await Assert.That(Read(Answer(quote: "pacific   power statement date")).Correspondent).IsNotNull();
	}

	[Test]
	public async Task AQuoteThatIsNotInTheDocument_MeansNoCorrespondent()
	{
		var suggestion = Read(Answer(quote: "Pacific Power Company, Portland"));

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.CorrespondentTrace!.Outcome).IsEqualTo("the quote isn't in the document");
	}

	[Test]
	[Arguments(null)]
	[Arguments("")]
	[Arguments("  ")]
	[Arguments("PP")]
	public async Task NoQuote_OrAUselessOne_MeansNoCorrespondent(string? quote)
	{
		var suggestion = Read(Answer(quote: quote));

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.CorrespondentTrace!.Outcome).IsEqualTo("no usable quote");
	}

	[Test]
	[Arguments("probable")]
	[Arguments("unsure")]
	[Arguments("")]
	[Arguments("very")]
	public async Task AnythingButCertain_IsNotSuggested_ForEitherKind(string confidence)
	{
		var suggestion = Read(Answer(confidence: confidence, typeConfidence: confidence));

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.DocumentType).IsNull();
	}

	[Test]
	[Arguments("Some Other Company")]
	[Arguments("Pacific Power Co")] // close, but not a name on the list
	public async Task ANameThatIsNotOnTheList_IsNotSuggested(string name)
	{
		var suggestion = Read(Answer(correspondent: name, documentType: name));

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.DocumentType).IsNull();
		await Assert.That(suggestion.CorrespondentTrace!.Outcome).IsEqualTo("not on Paperless's list");
	}

	[Test]
	public async Task ADocumentType_NeedsNoQuote_ButACorrespondentDoes()
	{
		var suggestion = Read(Answer(quote: null));

		await Assert.That(suggestion.DocumentType).IsNotNull();
		await Assert.That(suggestion.Correspondent).IsNull();
	}

	[Test]
	public async Task NullMeansNoneChosen()
	{
		var suggestion = Read(Answer(correspondent: null, quote: null, documentType: null, confidence: "unsure", typeConfidence: "unsure"));

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.DocumentType).IsNull();
		await Assert.That(suggestion.CorrespondentTrace!.Outcome).IsEqualTo("none chosen");
	}

	[Test]
	public async Task WithNoListsFromPaperless_NothingIsSuggested_AndTheTitleAndDateStillCome()
	{
		var suggestion = Read(Answer(), lists: false);

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.Title).IsEqualTo("Electric bill");
		await Assert.That(suggestion.Date).IsEqualTo(new DateOnly(2024, 3, 1));
	}

	[Test]
	public async Task AnAnswerThatIsNotAnObject_IsReportedNotCrashedOn()
	{
		await Assert.That(() => Read("[1, 2]")).Throws<NamingException>();
		await Assert.That(() => Read("not json")).Throws<NamingException>();
	}

	// ---- through the model call: what is asked, and what the lists do to it

	private sealed class Model(string content) : HttpMessageHandler
	{
		public string? Body { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Body = await request.Content!.ReadAsStringAsync(cancellationToken);

			var completion = JsonSerializer.Serialize(new {
				id = "x",
				@object = "chat.completion",
				created = 1,
				model = "test",
				choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
				usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
			});

			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion, Encoding.UTF8, "application/json") };
		}
	}

	private sealed class FakeTesseract(string text) : Tesseract(Options.Create(new NamingOptions()))
	{
		public override Task<string> ReadAsync(IReadOnlyList<string> pageFiles, CancellationToken cancellationToken) => Task.FromResult(text);
	}

	private static NameSuggester Suggester(Model model, ILogger<NameSuggester>? logger = null) =>
		new(
			Options.Create(new NamingOptions { Endpoint = new Uri("https://example.test/"), ApiKey = "k", Model = "m" }),
			new FakeTesseract(Text),
			new OpenAIClient(new System.ClientModel.ApiKeyCredential("k"), new OpenAIClientOptions { Endpoint = new Uri("https://example.test/openai/v1/"), Transport = new HttpClientPipelineTransport(new HttpClient(model)) }).GetChatClient("m").AsIChatClient(),
			logger);

	[Test]
	public async Task TheModelIsShownTheListsAndHeldToThem()
	{
		var model = new Model(Answer());
		var suggestion = await Suggester(model).SuggestAsync(["p.jpg"], new PaperlessChoices(Correspondents, DocumentTypes), CancellationToken.None);

		await Assert.That(suggestion.Correspondent!.Id).IsEqualTo(7);

		// the names are in the prompt, and in the shape of the answer as the only values allowed
		var body = JsonDocument.Parse(model.Body!).RootElement;
		var schema = body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
		var allowed = schema.GetProperty("properties").GetProperty("correspondent").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();

		await Assert.That(allowed).IsEquivalentTo(["Pacific Power", "Acme Insurance", null]);
		await Assert.That(schema.GetProperty("properties").GetProperty("documentType").GetProperty("enum").GetArrayLength()).IsEqualTo(3);
		await Assert.That(schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())).Contains("correspondentQuote");
		await Assert.That(schema.GetProperty("additionalProperties").GetBoolean()).IsFalse();
		await Assert.That(model.Body).Contains("Acme Insurance");
		await Assert.That(model.Body).Contains("not who it is addressed to");
	}

	[Test]
	public async Task WithoutLists_TheModelIsNotAskedForCorrespondents()
	{
		var model = new Model("""{"title":"Electric bill","documentDate":null}""");

		await Suggester(model).SuggestAsync(["p.jpg"], null, CancellationToken.None);

		await Assert.That(model.Body).DoesNotContain("correspondent");
		await Assert.That(model.Body).DoesNotContain("Correspondents already on file");
	}

	[Test]
	public async Task TwoEntriesWithTheSameName_AreNotOffered_BecauseTheNameCantSayWhichIsMeant()
	{
		var twice = new PaperlessChoices([new(7, "Pacific Power"), new(8, "pacific power"), new(9, "Acme Insurance")], DocumentTypes);
		var model = new Model(Answer(correspondent: "Pacific Power"));
		var suggestion = await Suggester(model).SuggestAsync(["p.jpg"], twice, CancellationToken.None);

		await Assert.That(suggestion.Correspondent).IsNull();

		var schema = JsonDocument.Parse(model.Body!).RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");

		await Assert.That(schema.GetProperty("properties").GetProperty("correspondent").GetProperty("enum").EnumerateArray().Select(e => e.GetString())).IsEquivalentTo(["Acme Insurance", null]);
	}

	[Test]
	public async Task AListTooLongToBeUseful_IsLeftToPaperless()
	{
		var many = new PaperlessChoices([.. Enumerable.Range(1, 1001).Select(i => new PaperlessChoice(i, $"Company {i}"))], DocumentTypes);
		var model = new Model(Answer());
		var suggestion = await Suggester(model).SuggestAsync(["p.jpg"], many, CancellationToken.None);

		await Assert.That(suggestion.Correspondent).IsNull();
		await Assert.That(suggestion.CorrespondentTrace!.Outcome).StartsWith("not asked");
		await Assert.That(model.Body).DoesNotContain("Company 1\\n");
		await Assert.That(suggestion.DocumentType).IsNotNull(); // the other list is still used
	}
}
