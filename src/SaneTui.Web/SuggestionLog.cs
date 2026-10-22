using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SaneTui.Web;

/// <summary>
/// A record of what the model suggested and what was then done with it, so the suggestions can be judged against what was actually
/// sent to Paperless. It is written to the log (standard output, which the container's log driver collects), one JSON object on a
/// line, under the category <see cref="Category"/>, so it can be picked out of the rest. Nothing is kept on disk by the service
/// itself. The text of a document never goes in, only the model's answers: a title, a date, the names it chose, and the short quote
/// it gave for a correspondent.
/// </summary>
internal sealed class SuggestionLog(ILoggerFactory loggers, TimeProvider time)
{
	public const string Category = "SaneTui.Suggestions";

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a line in a log, not a page: names stay readable
	};

	private readonly ILogger _log = loggers.CreateLogger(Category);

	/// <summary>What the model said, and what became of each choice it made.</summary>
	public void Suggested(string document, NameSuggestion suggestion) => Write(new {
		@event = "suggested",
		document,
		title = suggestion.Title,
		date = suggestion.Date?.ToString("yyyy-MM-dd"),
		correspondent = suggestion.CorrespondentTrace,
		documentType = suggestion.DocumentTypeTrace,
	});

	/// <summary>What was sent to Paperless, to set beside what was suggested.</summary>
	public void Sent(string document, string task, PaperlessMetadata sent, string? correspondentName, string? documentTypeName) => Write(new {
		@event = "sent",
		document,
		task,
		title = sent.Title,
		date = sent.Created?.ToString("yyyy-MM-dd"),
		correspondent = sent.CorrespondentId is { } c ? new { id = c, name = correspondentName } : null,
		documentType = sent.DocumentTypeId is { } d ? new { id = d, name = documentTypeName } : null,
	});

	/// <summary>How Paperless got on with it.</summary>
	public void Consumed(string document, string task, string status, int? paperlessDocument, string? message) => Write(new {
		@event = "consumed",
		document,
		task,
		status,
		paperlessDocument,
		message,
	});

	private void Write<T>(T entry)
	{
		var line = JsonSerializer.SerializeToNode(entry, Json)!.AsObject();

		line.Insert(0, "at", time.GetUtcNow()); // first, so the line reads in order

		_log.LogInformation("{Entry}", line.ToJsonString(Json));
	}
}
