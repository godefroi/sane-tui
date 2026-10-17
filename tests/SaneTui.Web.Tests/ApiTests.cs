using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;

namespace SaneTui.Web.Tests;

// The test scanner is one shared device per process, so the tests that scan can't overlap.
[NotInParallel("test-backend")]
public class ApiTests
{
	private static void RequireLibsane()
	{
		if (!NativeLibrary.TryLoad("libsane.so.1", out var handle)) {
			Skip.Test("libsane isn't installed here; run these in the container (see tests/Dockerfile).");
		}

		NativeLibrary.Free(handle);
	}

	private static async Task<JsonElement> StateAsync(HttpClient client) =>
		JsonDocument.Parse(await client.GetStringAsync("/api/state")).RootElement.Clone();

	private static async Task<JsonElement> WaitUntilIdleAsync(HttpClient client)
	{
		for (var i = 0; i < 300; i++) {
			var state = await StateAsync(client);

			if (state.GetProperty("status").GetString() == "idle") {
				return state;
			}

			await Task.Delay(100);
		}

		throw new TimeoutException("The scanner never went idle.");
	}

	private static Task<HttpResponseMessage> Scan(HttpClient client, object body) => client.PostAsJsonAsync("/api/scan", body);

	[Test]
	public async Task TheFirstEvent_IsTheWholeState()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();
		using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cancel.Token);

		await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");

		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
		var line = await reader.ReadLineAsync(cancel.Token);

		await Assert.That(line).StartsWith("data: ");

		var state = JsonDocument.Parse(line![6..]).RootElement;

		await Assert.That(state.GetProperty("status").GetString()).IsEqualTo("idle");
		await Assert.That(state.GetProperty("log").GetArrayLength()).IsEqualTo(0);
	}

	[Test]
	public async Task OpenEventStreams_EndWhenTheServiceIsToldToStop()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();
		using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cancel.Token);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));

		await reader.ReadLineAsync(cancel.Token); // connected, and given the state

		// a stream that never ends would keep a real host waiting out its whole shutdown timeout, so a stop seemed to hang
		app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();

		var ended = false;

		try {
			using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);

			wait.CancelAfter(TimeSpan.FromSeconds(5));

			while (await reader.ReadLineAsync(wait.Token) is not null) {
			}

			ended = true;
		} catch (OperationCanceledException) {
		}

		await Assert.That(ended).IsTrue();
	}

	[Test]
	public async Task AScanRequestThatMakesNoSense_IsRefusedBeforeTouchingTheScanner()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();

		var bad = new object[] {
			new { device = "", resolution = 100 },
			new { device = "test", resolution = 0 },
			new { device = "test", resolution = 99999 },
			new { device = "test", resolution = 100, width = 50 }, // an area needs all four numbers
			new { device = "test", resolution = 100, left = -1, top = 0, width = 50, height = 50 },
			new { device = "test", resolution = 100, left = 0, top = 0, width = 0, height = 50 },
			new { device = "test", resolution = 100, paper = "a4", left = 0, top = 0, width = 50, height = 50 }, // a paper size and an area
			new { device = "test", resolution = 100, paper = "nonsense" },
			new { device = "test", resolution = 100, settings = new[] { "mode=Color" } }, // only on/off options are accepted
			new { device = "test", resolution = 100, settings = new[] { "../x=true" } },
		};

		foreach (var body in bad) {
			var response = await Scan(client, body);

			await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
			await Assert.That(await response.Content.ReadAsStringAsync()).Contains("error");
		}

		await Assert.That((await StateAsync(client)).GetProperty("status").GetString()).IsEqualTo("idle");
	}

	[Test]
	public async Task NoDocument_MeansNoPagesAndNoPdf()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();

		await Assert.That((await client.GetAsync("/api/pages/p0001")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
		await Assert.That((await client.GetAsync("/api/document.pdf")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
		await Assert.That((await client.GetAsync("/api/preview.jpg")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
		await Assert.That((await StateAsync(client)).GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
	}

	[Test]
	public async Task ScanningTheTestDevice_AddsAPage_ThatCanBeFetchedThumbnailedAndMadeIntoAPdf()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		await Assert.That((await Scan(client, new { device = "test", resolution = 50 })).StatusCode).IsEqualTo(HttpStatusCode.Accepted);

		var state = await WaitUntilIdleAsync(client);
		var pages = state.GetProperty("document").GetProperty("pages");

		await Assert.That(pages.GetArrayLength()).IsEqualTo(1);
		await Assert.That(state.GetProperty("log").EnumerateArray().Any(l => l.GetProperty("error").GetBoolean())).IsFalse();

		var id = pages[0].GetProperty("id").GetString();
		var jpeg = await client.GetByteArrayAsync($"/api/pages/{id}");
		var thumb = await client.GetByteArrayAsync($"/api/pages/{id}?w=40");

		await Assert.That(new MagickImageInfo(jpeg).Width).IsEqualTo((uint)pages[0].GetProperty("width").GetInt32());
		await Assert.That(new MagickImageInfo(thumb).Width).IsEqualTo(40u);

		var pdf = await client.GetByteArrayAsync("/api/document.pdf");

		await Assert.That(pdf.AsSpan().IndexOf(jpeg)).IsGreaterThanOrEqualTo(0);

		// a second scan adds to the same document
		await Scan(client, new { device = "test", resolution = 50 });

		await Assert.That((await WaitUntilIdleAsync(client)).GetProperty("document").GetProperty("pages").GetArrayLength()).IsEqualTo(2);

		await Assert.That((await client.DeleteAsync($"/api/pages/{id}")).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
		await Assert.That((await client.DeleteAsync("/api/document")).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
		await Assert.That((await StateAsync(client)).GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
	}

	[Test]
	public async Task ScanningAFeeder_WithAllPages_SavesEverySheet()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		await Scan(client, new { device = "test", source = "Automatic Document Feeder", resolution = 50, allPages = true });

		var state = await WaitUntilIdleAsync(client);

		await Assert.That(state.GetProperty("document").GetProperty("pages").GetArrayLength()).IsGreaterThan(1);
		await Assert.That(state.GetProperty("log").EnumerateArray().Select(l => l.GetProperty("text").GetString()).Any(t => t!.StartsWith("The feeder is empty."))).IsTrue();
	}

	[Test]
	public async Task APreview_ProducesAPicture_AndTheBedItCovers()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		await Assert.That((await client.PostAsJsonAsync("/api/preview", new { device = "test" })).StatusCode).IsEqualTo(HttpStatusCode.Accepted);

		var preview = (await WaitUntilIdleAsync(client)).GetProperty("preview");

		await Assert.That(preview.GetProperty("area").GetProperty("width").GetDouble()).IsGreaterThan(0);

		var picture = await client.GetByteArrayAsync("/api/preview.jpg");

		await Assert.That(new MagickImageInfo(picture).Width).IsEqualTo((uint)preview.GetProperty("pixelWidth").GetInt32());
	}

	[Test]
	public async Task TheCapabilities_ComeFromTheScanner()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		var caps = JsonDocument.Parse(await client.GetStringAsync("/api/capabilities?device=test")).RootElement;

		await Assert.That(caps.GetProperty("sources").GetProperty("choices").EnumerateArray().Select(e => e.GetString())).Contains("Flatbed");
		await Assert.That(caps.GetProperty("feeders").EnumerateArray().Select(e => e.GetString())).Contains("Automatic Document Feeder");
		await Assert.That(caps.GetProperty("defaultResolution").GetDouble()).IsEqualTo(300d);
	}

	[Test]
	public async Task TheCapabilitiesForAnotherSource_AreReadWithThatSourceSelected_AndOfferTheScannersOnOffOptions()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		var caps = JsonDocument.Parse(await client.GetStringAsync("/api/capabilities?device=test&source=Automatic%20Document%20Feeder")).RootElement;

		await Assert.That(caps.GetProperty("sources").GetProperty("selected").GetString()).IsEqualTo("Automatic Document Feeder");

		var switches = caps.GetProperty("switches").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();

		await Assert.That(switches).Contains("read-limit");
		await Assert.That(switches).DoesNotContain("preview"); // the Preview button's own business
	}

	[Test]
	public async Task AScanWithCorrectionsAndAScannerOption_Works()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		await Scan(client, new { device = "test", resolution = 50, deskew = true, trim = true, settings = new[] { "read-limit=true" } });

		var state = await WaitUntilIdleAsync(client);

		await Assert.That(state.GetProperty("document").GetProperty("pages").GetArrayLength()).IsEqualTo(1);
		await Assert.That(state.GetProperty("log").EnumerateArray().Any(l => l.GetProperty("error").GetBoolean())).IsFalse();
	}

	[Test]
	public async Task ABlankSourceOrMode_MeansLeaveItAlone()
	{
		RequireLibsane();

		using var app = new AppFactory();
		using var client = app.SignedIn();

		// a scanner with no choice of source has an empty dropdown, which a page can send as ""
		await Assert.That((await Scan(client, new { device = "test", source = "", mode = "  ", resolution = 50 })).StatusCode).IsEqualTo(HttpStatusCode.Accepted);

		var state = await WaitUntilIdleAsync(client);

		await Assert.That(state.GetProperty("document").GetProperty("pages").GetArrayLength()).IsEqualTo(1);
		await Assert.That(state.GetProperty("log").EnumerateArray().Any(l => l.GetProperty("error").GetBoolean())).IsFalse();

		await client.PostAsJsonAsync("/api/preview", new { device = "test", source = "" });

		await Assert.That((await WaitUntilIdleAsync(client)).GetProperty("log").EnumerateArray().Any(l => l.GetProperty("error").GetBoolean())).IsFalse();
	}
}
