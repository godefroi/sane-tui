using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SaneTui.Web.Tests;

internal sealed class AppFactory(string? password = "secret", bool anonymous = false, Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
	public string Data { get; } = Directory.CreateTempSubdirectory("sanetui-web-app-").FullName;

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseSetting("Web:DataFolder", Data);
		builder.ConfigureServices(services => configureServices?.Invoke(services));
		builder.UseSetting("Web:User", "scan");
		builder.UseSetting("Web:AllowAnonymous", anonymous ? "true" : "false");

		if (password is not null) {
			builder.UseSetting("Web:Password", password);
		}
	}

	public HttpClient SignedIn(string user = "scan", string pass = "secret")
	{
		var client = CreateClient();

		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

		return client;
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

		if (disposing && Directory.Exists(Data)) {
			Directory.Delete(Data, recursive: true);
		}
	}
}

public class AuthTests
{
	[Test]
	public async Task WithoutCredentials_EveryRoute_AsksForThem()
	{
		using var app = new AppFactory();
		using var client = app.CreateClient();

		foreach (var path in new[] { "/", "/api/state", "/api/events", "/api/preview.jpg", "/api/document.pdf" }) {
			var response = await client.GetAsync(path);

			await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
			await Assert.That(response.Headers.WwwAuthenticate.ToString()).StartsWith("Basic");
		}
	}

	[Test]
	[Arguments("scan", "wrong")]
	[Arguments("someone", "secret")]
	[Arguments("", "")]
	public async Task WrongCredentials_AreRefused(string user, string password)
	{
		using var app = new AppFactory();
		using var client = app.SignedIn(user, password);

		await Assert.That((await client.GetAsync("/api/state")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task RightCredentials_GetIn()
	{
		using var app = new AppFactory();
		using var client = app.SignedIn();

		await Assert.That((await client.GetAsync("/api/state")).StatusCode).IsEqualTo(HttpStatusCode.OK);
	}

	[Test]
	public async Task APasswordWithAColonInIt_Works()
	{
		using var app = new AppFactory(password: "a:b:c");
		using var client = app.SignedIn(pass: "a:b:c");

		await Assert.That((await client.GetAsync("/api/state")).StatusCode).IsEqualTo(HttpStatusCode.OK);
	}

	[Test]
	public async Task WithNoPasswordConfigured_TheServiceWillNotStart()
	{
		using var app = new AppFactory(password: null);

		await Assert.That(() => app.CreateClient()).Throws<InvalidOperationException>();
	}

	[Test]
	public async Task AnonymousMode_MustBeAskedFor_AndThenNeedsNoCredentials()
	{
		using var app = new AppFactory(password: null, anonymous: true);
		using var client = app.CreateClient();

		await Assert.That((await client.GetAsync("/api/state")).StatusCode).IsEqualTo(HttpStatusCode.OK);
	}

	[Test]
	[Arguments("Basic !!!not base64!!!")]
	[Arguments("Basic " + "bm9jb2xvbg==")] // "nocolon"
	[Arguments("Bearer abc")]
	[Arguments("")]
	public async Task MalformedHeaders_AreRefusedWithoutAnError(string header)
	{
		await Assert.That(BasicAuth.Matches(header, "scan", "secret")).IsFalse();
	}
}
