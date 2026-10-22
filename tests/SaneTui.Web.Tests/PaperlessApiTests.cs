using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SaneTui.Sane;

namespace SaneTui.Web.Tests;

public class PaperlessApiTests
{
	private sealed class Paperless : HttpMessageHandler
	{
		public int Uploads { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var path = request.RequestUri!.AbsolutePath;
			string body;

			if (path == "/api/documents/post_document/") {
				Uploads++;
				body = "\"task-1\"";
			} else if (path == "/api/tasks/") {
				body = """{"results":[{"status":"started"}]}""";
			} else {
				body = JsonSerializer.Serialize(new { next = (string?)null, results = new[] { new { id = 7, name = path.Contains("correspondents") ? "Pacific Power" : "Statement" } } });
			}

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
		}
	}

	private static AppFactory Configured(Paperless paperless) => new(configureServices: services => {
		services.Configure<PaperlessOptions>(o => {
			o.Url = new Uri("http://paperless.test/");
			o.Token = "t";
			o.PublicUrl = new Uri("https://docs.example/");
		});
		services.AddSingleton(sp => {
			var http = new HttpClient(paperless) { BaseAddress = new Uri("http://paperless.test/") };

			PaperlessClient.Configure(http, "t");

			return new PaperlessClient(http, TimeProvider.System);
		});
	});

	private static void AddAPage(AppFactory app) =>
		app.Services.GetRequiredService<DocumentStore>().AddPage(new SaneImage(8, 8, SanePixelFormat.Gray, 8, 8, new byte[64]), 100, default);

	[Test]
	public async Task WithoutSettings_TheFeatureIsOff_AndSaysSo()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();

		var config = await client.GetFromJsonAsync<JsonElement>("/api/config");

		await Assert.That(config.GetProperty("paperless").GetBoolean()).IsFalse();
		await Assert.That((await client.GetAsync("/api/paperless/choices")).StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

		AddAPage(app);

		var response = await client.PostAsJsonAsync("/api/document/paperless", new { title = "x" });

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
	}

	[Test]
	public async Task WithSettings_ConfigSaysSo_AndGivesTheAddressForLinks()
	{
		using var app = Configured(new Paperless());
		using var client = app.SignedIn();

		var config = await client.GetFromJsonAsync<JsonElement>("/api/config");

		await Assert.That(config.GetProperty("paperless").GetBoolean()).IsTrue();
		await Assert.That(config.GetProperty("paperlessUrl").GetString()).IsEqualTo("https://docs.example/");

		var choices = await client.GetFromJsonAsync<JsonElement>("/api/paperless/choices");

		await Assert.That(choices.GetProperty("correspondents")[0].GetProperty("name").GetString()).IsEqualTo("Pacific Power");
		await Assert.That(choices.GetProperty("documentTypes")[0].GetProperty("name").GetString()).IsEqualTo("Statement");
	}

	[Test]
	public async Task Send_IsAccepted_ThenShowsInTheDocument_AndASecondIsRefused()
	{
		var paperless = new Paperless();

		using var app = Configured(paperless);
		using var client = app.SignedIn();

		AddAPage(app);

		var response = await client.PostAsJsonAsync("/api/document/paperless", new { title = "Electric bill", date = "2024-03-01", correspondentId = 7, documentTypeId = 7 });

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
		await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()).IsEqualTo("consuming");
		await Assert.That(app.Services.GetRequiredService<DocumentStore>().Current!.Sent!.TaskId).IsEqualTo("task-1");

		var again = await client.PostAsJsonAsync("/api/document/paperless", new { title = "Electric bill", again = true });

		await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
		await Assert.That(paperless.Uploads).IsEqualTo(1);
	}

	[Test]
	public async Task Send_WithABadRequest_Is400_NotAnUpload()
	{
		var paperless = new Paperless();

		using var app = Configured(paperless);
		using var client = app.SignedIn();

		AddAPage(app);

		var response = await client.PostAsJsonAsync("/api/document/paperless", new { date = "yesterday" });

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
		await Assert.That(paperless.Uploads).IsEqualTo(0);
	}

	[Test]
	public async Task Send_NeedsASignIn()
	{
		using var app = Configured(new Paperless());
		using var client = app.CreateClient();

		var response = await client.PostAsJsonAsync("/api/document/paperless", new { title = "x" });

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
	}
}
