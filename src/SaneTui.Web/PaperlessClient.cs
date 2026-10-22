using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SaneTui.Web;

/// <summary>Settings for sending documents to Paperless-ngx (configuration section <c>Paperless</c>). Without a URL and a token the feature is off.</summary>
internal sealed class PaperlessOptions
{
	/// <summary>Where this service reaches Paperless, such as <c>http://paperless.local:8000</c>.</summary>
	public Uri? Url { get; set; }

	public string? Token { get; set; }

	/// <summary>Where a person's browser reaches Paperless, for links to a document, if that differs from <see cref="Url"/> (a container often reaches it by another name).</summary>
	public Uri? PublicUrl { get; set; }

	public bool Available => Url is not null && !string.IsNullOrWhiteSpace(Token);

	/// <summary>The address with a trailing slash, which relative request paths need to be added to it properly.</summary>
	public Uri? BaseUri => Slashed(Url);

	public Uri? LinkBase => Slashed(PublicUrl ?? Url);

	private static Uri? Slashed(Uri? uri) => uri is null || uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

internal sealed record PaperlessChoice(int Id, string Name);

internal sealed record PaperlessChoices(IReadOnlyList<PaperlessChoice> Correspondents, IReadOnlyList<PaperlessChoice> DocumentTypes);

/// <param name="Title">Null or empty lets Paperless take the title from the file name.</param>
/// <param name="Created">The document's own date; null lets Paperless find one in the text.</param>
internal sealed record PaperlessMetadata(string? Title, DateOnly? Created, int? CorrespondentId, int? DocumentTypeId);

internal enum PaperlessTaskState
{
	Waiting,
	Done,
	Failed,
}

/// <param name="DocumentId">The document Paperless made, when it did.</param>
/// <param name="Message">Why it failed, or what Paperless said about a stopped one.</param>
internal sealed record PaperlessTaskResult(PaperlessTaskState State, int? DocumentId, string? Message);

/// <summary>Talking to Paperless failed; the message says why, in words for the person using the page.</summary>
internal sealed class PaperlessException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Paperless-ngx through its REST API. Requests ask for API version 10, so that a newer default on the server doesn't change what
/// comes back (the task format changed at version 10).
/// </summary>
internal sealed class PaperlessClient(HttpClient http, TimeProvider time)
{
	// asks the server for this many at a time; it allows up to 100,000
	private const int PageSize = 1000;

	// a guard against a server that never says it has reached the last page
	private const int MaxPages = 100;

	private static readonly TimeSpan ChoicesLifetime = TimeSpan.FromMinutes(5);

	private readonly SemaphoreSlim _choicesLock = new(1, 1);
	private (DateTimeOffset At, PaperlessChoices Choices)? _choices;

	/// <summary>Builds the client from the settings: the token, the API version, and connections that don't hold on to an old address for ever.</summary>
	public static PaperlessClient Create(PaperlessOptions options, TimeProvider time)
	{
		var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) {
			BaseAddress = options.BaseUri,
			Timeout = TimeSpan.FromMinutes(5), // a large PDF takes a while to upload
		};

		Configure(client, options.Token);

		return new PaperlessClient(client, time);
	}

	internal static void Configure(HttpClient client, string? token)
	{
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token);
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json; version=10");
	}

	/// <summary>The correspondents and document types on file. Kept for a few minutes: they change rarely and every suggestion wants them.</summary>
	public async Task<PaperlessChoices> GetChoicesAsync(CancellationToken cancellationToken)
	{
		await _choicesLock.WaitAsync(cancellationToken);

		try {
			if (_choices is { } known && time.GetUtcNow() - known.At < ChoicesLifetime) {
				return known.Choices;
			}

			var choices = new PaperlessChoices(await ListAsync("api/correspondents/", cancellationToken), await ListAsync("api/document_types/", cancellationToken));

			_choices = (time.GetUtcNow(), choices);

			return choices;
		} finally {
			_choicesLock.Release();
		}
	}

	/// <summary>Uploads a PDF to be consumed, with what is known about it. Returns the id of the consumption task, which says later how it went.</summary>
	public async Task<string> UploadAsync(Stream pdf, string fileName, PaperlessMetadata metadata, CancellationToken cancellationToken)
	{
		using var form = new MultipartFormDataContent();
		using var document = new StreamContent(pdf);

		document.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
		form.Add(document, "document", fileName);

		if (!string.IsNullOrWhiteSpace(metadata.Title)) {
			form.Add(new StringContent(metadata.Title), "title");
		}

		if (metadata.Created is { } created) {
			form.Add(new StringContent(created.ToString("yyyy-MM-dd")), "created");
		}

		if (metadata.CorrespondentId is { } correspondent) {
			form.Add(new StringContent(correspondent.ToString()), "correspondent");
		}

		if (metadata.DocumentTypeId is { } documentType) {
			form.Add(new StringContent(documentType.ToString()), "document_type");
		}

		using var response = await SendAsync(() => http.PostAsync("api/documents/post_document/", form, cancellationToken), "upload the document");

		await EnsureSuccessAsync(response, "upload the document", cancellationToken);

		// the answer is the task's id, as a JSON string
		var body = await response.Content.ReadAsStringAsync(cancellationToken);

		try {
			return JsonSerializer.Deserialize<string>(body) is { Length: > 0 } id ? id : throw new PaperlessException("Paperless took the document but didn't say which task is consuming it.");
		} catch (JsonException ex) {
			throw new PaperlessException("Paperless took the document but its answer wasn't what was expected.", ex);
		}
	}

	/// <summary>How a consumption task is getting on. A task Paperless hasn't recorded yet counts as waiting.</summary>
	public async Task<PaperlessTaskResult> GetTaskAsync(string taskId, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(() => http.GetAsync($"api/tasks/?task_id={Uri.EscapeDataString(taskId)}", cancellationToken), "ask how the document is getting on");

		await EnsureSuccessAsync(response, "ask how the document is getting on", cancellationToken);

		JsonElement root;

		try {
			root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement;
		} catch (JsonException ex) {
			throw new PaperlessException("Paperless's answer about the task wasn't what was expected.", ex);
		}

		// version 10 pages the list; an older one gave a plain array
		var tasks = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("results", out var results) ? results : default;

		if (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0) {
			return new PaperlessTaskResult(PaperlessTaskState.Waiting, null, null);
		}

		return ReadTask(tasks[0]);
	}

	/// <summary>What a task object says: its status, and in <c>result_data</c> either the document it made, the one it duplicated, or why it failed.</summary>
	internal static PaperlessTaskResult ReadTask(JsonElement task)
	{
		var status = task.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
		var result = task.TryGetProperty("result_data", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;

		int? Number(string name) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
		string? Text(string name) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

		switch (status) {
			case "pending" or "started":
				return new PaperlessTaskResult(PaperlessTaskState.Waiting, null, null);
			case "success":
				if (Number("document_id") is { } created) {
					return new PaperlessTaskResult(PaperlessTaskState.Done, created, null);
				}

				// stopped without making a document: because it's a duplicate that Paperless is set to refuse, or for a reason it gives
				return Number("duplicate_of") is { } duplicate
					? new PaperlessTaskResult(PaperlessTaskState.Failed, null, $"Paperless didn't keep it: it's a duplicate of document #{duplicate}.")
					: new PaperlessTaskResult(PaperlessTaskState.Failed, null, Text("reason") ?? "Paperless finished without making a document.");
			case "failure" or "revoked":
				return new PaperlessTaskResult(PaperlessTaskState.Failed, null, Text("error_message") ?? Text("reason") ?? $"Paperless reported the task as {status}.");
			default:
				return new PaperlessTaskResult(PaperlessTaskState.Waiting, null, null);
		}
	}

	// every page of a list of {id, name}, asked for by page number (the "next" address in an answer is built from what the server
	// thinks its own address is, which is wrong behind a proxy)
	private async Task<IReadOnlyList<PaperlessChoice>> ListAsync(string path, CancellationToken cancellationToken)
	{
		var choices = new List<PaperlessChoice>();

		for (var page = 1; page <= MaxPages; page++) {
			using var response = await SendAsync(() => http.GetAsync($"{path}?page_size={PageSize}&page={page}", cancellationToken), "read its lists");

			await EnsureSuccessAsync(response, "read its lists", cancellationToken);

			JsonElement root;

			try {
				root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement;
			} catch (JsonException ex) {
				throw new PaperlessException($"Paperless's answer for {path} wasn't what was expected: is Paperless__Url the address of Paperless itself?", ex);
			}

			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) {
				throw new PaperlessException($"Paperless's answer for {path} wasn't a list: is Paperless__Url the address of Paperless itself?");
			}

			foreach (var item in results.EnumerateArray()) {
				if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text) {
					choices.Add(new PaperlessChoice(id.GetInt32(), text));
				}
			}

			if (!root.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null) {
				return choices;
			}
		}

		throw new PaperlessException($"Paperless's list at {path} has more than {MaxPages * PageSize} entries; stopped reading it.");
	}

	private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, string doing)
	{
		try {
			return await send();
		} catch (HttpRequestException ex) {
			throw new PaperlessException($"Couldn't reach Paperless to {doing}: {ex.Message}", ex);
		} catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested) {
			throw new PaperlessException($"Paperless took too long to {doing}.", ex);
		}
	}

	private static async Task EnsureSuccessAsync(HttpResponseMessage response, string doing, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode) {
			return;
		}

		var message = response.StatusCode switch {
			HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"Paperless refused to {doing} ({(int)response.StatusCode}): check Paperless__Token, and that its user is allowed to do this.",
			HttpStatusCode.NotFound => $"Paperless answered 404 when asked to {doing}: is Paperless__Url the address of Paperless itself?",
			_ => $"Paperless answered {(int)response.StatusCode} when asked to {doing}: {Excerpt(await response.Content.ReadAsStringAsync(cancellationToken))}",
		};

		throw new PaperlessException(message);
	}

	private static string Excerpt(string body)
	{
		body = body.Trim();

		return body.Length <= 200 ? body : body[..200] + "...";
	}
}
