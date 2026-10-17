using System.Text.Json;
using ImageMagick;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using SaneTui;
using SaneTui.Sane;
using SaneTui.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<WebOptions>(builder.Configuration.GetSection("Web"));
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<ScannerService>();
builder.Services.Configure<NamingOptions>(builder.Configuration.GetSection("Naming"));
builder.Services.AddSingleton<Tesseract>();
builder.Services.AddSingleton<NameSuggester>();

var app = builder.Build();
var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;

// Fail closed: an unprotected scanner service is something that has to be asked for.
if (!web.AllowAnonymous && string.IsNullOrEmpty(web.Password)) {
	throw new InvalidOperationException("No password is set. Set Web__Password (and optionally Web__User), or Web__AllowAnonymous=true for local development only.");
}

// what went wrong becomes a status the page can act on: busy is not a failure of the request, a bad request is the caller's to fix
app.UseExceptionHandler(errors => errors.Run(async context => {
	var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
	var (status, message) = error switch {
		BusyException => (StatusCodes.Status409Conflict, error.Message),
		CommandException => (StatusCodes.Status400BadRequest, error.Message),
		SaneException => (StatusCodes.Status502BadGateway, error.Message),
		NamingException => (StatusCodes.Status502BadGateway, error.Message),
		_ => (StatusCodes.Status500InternalServerError, "Something unexpected went wrong."),
	};

	context.Response.StatusCode = status;

	await context.Response.WriteAsJsonAsync(new { error = message });
}));

if (!web.AllowAnonymous) {
	app.Use(async (context, next) => {
		if (BasicAuth.Matches(context.Request.Headers.Authorization.ToString(), web.User, web.Password!)) {
			await next(context);

			return;
		}

		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		context.Response.Headers.WWWAuthenticate = "Basic realm=\"sane-tui\", charset=\"UTF-8\"";
	});
}

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/state", (ScannerService scanner) => scanner.GetSnapshot());

// Server-sent events: the whole state on connect and after every change. Browsers reconnect by themselves, and a reconnect gets
// a fresh snapshot, so there's nothing to replay. (No heartbeat: an idle connection a proxy drops is simply reconnected.)
api.MapGet("/events", async (HttpContext context, ScannerService scanner, IOptions<JsonOptions> json, CancellationToken cancellationToken) => {
	context.Response.Headers.ContentType = "text/event-stream";
	context.Response.Headers.CacheControl = "no-cache";
	context.Response.Headers["X-Accel-Buffering"] = "no";

	try {
		await foreach (var snapshot in scanner.WatchAsync(cancellationToken)) {
			await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(snapshot, json.Value.SerializerOptions)}\n\n", cancellationToken);
			await context.Response.Body.FlushAsync(cancellationToken);
		}
	} catch (OperationCanceledException) {
		// the browser went away
	}
});

api.MapGet("/devices", async (ScannerService scanner, bool? refresh) =>
	(await scanner.GetDevicesAsync(refresh ?? false)).Select(d => new { d.Name, d.Vendor, d.Model, d.Type }));

api.MapGet("/capabilities", async (ScannerService scanner, string device, string? source) => await scanner.GetCapabilitiesAsync(device, source));

api.MapPost("/preview", (ScannerService scanner, PreviewRequest request) => {
	Require(!string.IsNullOrWhiteSpace(request.Device), "Choose a scanner first.");

	scanner.StartPreview(request);

	return Results.Accepted();
});

api.MapGet("/preview.jpg", (ScannerService scanner) =>
	scanner.PreviewImage is { } bytes ? Results.File(bytes, "image/jpeg") : Results.NotFound());

api.MapPost("/scan", (ScannerService scanner, ScanJobRequest job) => {
	Require(!string.IsNullOrWhiteSpace(job.Device), "Choose a scanner first.");
	Require(double.IsFinite(job.Resolution) && job.Resolution is > 0 and <= 4800, "The resolution isn't a sensible number of dots per inch.");
	Require(job.Left is null && job.Top is null && job.Width is null && job.Height is null || job.Left is >= 0 && job.Top is >= 0 && job.Width is > 0 && job.Height is > 0, "An area needs a left, top, width and height, all in millimeters and positive.");
	Require(new[] { job.Left, job.Top, job.Width, job.Height }.All(v => v is null || double.IsFinite(v.Value)), "The area has to be made of real numbers.");

	Require(job.Settings is null || job.Settings.Count <= 32 && job.Settings.All(s => System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z0-9_-]{1,64}=(true|false)$")), "Scanner options have to look like name=true or name=false.");

	scanner.StartScan(job);

	return Results.Accepted();
});

api.MapPost("/cancel", (ScannerService scanner) => {
	scanner.Cancel();

	return Results.Accepted();
});

// a page of the document: the full picture, or a thumbnail when a width is given
api.MapGet("/pages/{id}", async (DocumentStore documents, string id, int? w) => {
	if (documents.PagePath(id) is not { } path) {
		return Results.NotFound();
	}

	if (w is not > 0) {
		return Results.File(File.OpenRead(path), "image/jpeg");
	}

	var bytes = await Task.Run(() => {
		using var image = new MagickImage(path);

		image.Resize((uint)Math.Min(w.Value, 2000), 0);

		return image.ToByteArray(MagickFormat.Jpeg);
	});

	return Results.File(bytes, "image/jpeg");
});

api.MapDelete("/pages/{id}", (DocumentStore documents, ScannerService scanner, string id) => {
	if (!documents.DeletePage(id)) {
		return Results.NotFound();
	}

	scanner.Changed();

	return Results.NoContent();
});

// throws the document away, which is final; refused while a scan is still adding to it
api.MapDelete("/document", (DocumentStore documents, ScannerService scanner) => {
	if (scanner.GetSnapshot().Status != "idle") {
		throw new BusyException();
	}

	documents.Discard();
	scanner.Changed();

	return Results.NoContent();
});

// the title and date, if given, make the file's name; the browser saves it under that
api.MapGet("/document.pdf", async (DocumentStore documents, string? title, string? date) => {
	var pages = documents.PagePaths();

	if (pages.Count == 0) {
		return Results.NotFound();
	}

	var stream = await Task.Run(() => PdfBuilder.Build(pages));
	DateOnly? day = DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;

	return Results.File(stream, "application/pdf", DocumentNames.PdfFileName(title, day, DateTime.Now));
});

// what the page can offer, which depends on how the service was set up
api.MapGet("/config", (NameSuggester names) => new { naming = names.Available });

// Reads the first pages' text and asks the language model for a title and date. Slow (seconds), so it's a plain request that
// the page waits for; it doesn't use the scanner and doesn't block one.
api.MapPost("/document/name", async (DocumentStore documents, NameSuggester names, CancellationToken cancellationToken) => {
	if (!names.Available) {
		return Results.Json(new { error = "Suggesting names isn't set up on this service." }, statusCode: StatusCodes.Status503ServiceUnavailable);
	}

	var pages = documents.PagePaths();

	if (pages.Count == 0) {
		return Results.NotFound();
	}

	var suggestion = await names.SuggestAsync(pages, cancellationToken);

	return Results.Ok(new { title = suggestion.Title, date = suggestion.Date?.ToString("yyyy-MM-dd") });
});

app.Run();

static void Require(bool condition, string message)
{
	if (!condition) {
		throw new CommandException(message);
	}
}

/// <summary>Lets the integration tests start the app.</summary>
public partial class Program;
