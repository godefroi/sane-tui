using System.Globalization;
using Microsoft.Extensions.Options;

namespace SaneTui.Web;

/// <summary>The request can't be done in the state things are in (say, the document is already on its way).</summary>
internal sealed class ConflictException(string message) : Exception(message);

/// <param name="Title">Null or blank lets Paperless take the title from the file name.</param>
/// <param name="Date">The document's own date as <c>yyyy-MM-dd</c>, or null.</param>
/// <param name="Again">Send a document that was already sent, to make a second copy.</param>
internal sealed record SendRequest(string? Title, string? Date, int? CorrespondentId, int? DocumentTypeId, bool Again = false);

/// <summary>
/// Sends the current document to Paperless and follows it until Paperless has said how it went. Paperless takes a document in
/// at once but makes it later, in another process, so "sent" and "made" are two moments: the first is the upload, the second is
/// found by asking about the task it started. Where it stands is kept with the document, so a reload, another browser, or a restart of this
/// service all show the same thing (and a restart carries on asking).
/// </summary>
internal sealed class PaperlessService(PaperlessClient client, DocumentStore documents, ScannerService scanner, SuggestionLog log, IOptions<PaperlessOptions> options, TimeProvider time, ILogger<PaperlessService> logger) : IHostedService
{
	// asking about a task this many times in a row and failing means Paperless can't be reached
	private const int MaxPollFailures = 5;

	private readonly SemaphoreSlim _sending = new(1, 1);
	private readonly CancellationTokenSource _stopping = new();
	private readonly List<Task> _polling = [];

	/// <summary>How often to ask how a task is getting on.</summary>
	internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

	/// <summary>How long to keep asking. A long document takes a while to read, but not for ever.</summary>
	internal TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(15);

	public bool Available => options.Value.Available;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		// a document that was being made when the service last stopped: carry on asking
		if (Available && documents.Current is { Sent: { Status: "consuming" } sent } document) {
			Track(PollAsync(document.Id, sent.TaskId, sent.At));
		}

		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _stopping.CancelAsync();

		Task[] running;

		lock (_polling) {
			running = [.. _polling];
		}

		await Task.WhenAll(running).WaitAsync(cancellationToken);
	}

	/// <summary>The correspondents and document types Paperless has.</summary>
	public Task<PaperlessChoices> GetChoicesAsync(CancellationToken cancellationToken) => client.GetChoicesAsync(cancellationToken);

	/// <summary>Uploads the current document and starts following it. Returns once Paperless has taken it.</summary>
	/// <exception cref="BusyException">The scanner is in use, so the document may still be growing.</exception>
	/// <exception cref="ConflictException">It's already on its way, or was already sent and a second copy wasn't asked for.</exception>
	/// <exception cref="CommandException">The request doesn't make sense: nothing to send, a date that isn't one, or a correspondent or document type Paperless doesn't have.</exception>
	/// <exception cref="PaperlessException">Paperless couldn't be reached, or refused.</exception>
	public async Task<SendInfo> SendAsync(SendRequest request, CancellationToken cancellationToken)
	{
		if (scanner.IsBusy) {
			throw new BusyException();
		}

		if (!await _sending.WaitAsync(0, cancellationToken)) {
			throw new ConflictException("The document is already being sent.");
		}

		try {
			if (documents.Current is not { Pages.Count: > 0 } document) {
				throw new CommandException("There is nothing to send: scan a page first.");
			}

			if (document.Sent is { Status: "consuming" }) {
				throw new ConflictException("The document is already with Paperless, which is still working on it.");
			}

			if (document.Sent is { Status: "done" } && !request.Again) {
				throw new ConflictException("This document was already sent to Paperless. Send it again to make a second copy.");
			}

			DateOnly? created = null;

			if (!string.IsNullOrWhiteSpace(request.Date)) {
				created = DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
					? date
					: throw new CommandException("The date has to be a real date, written yyyy-MM-dd.");
			}

			// what the page sends is checked against what Paperless has: an id the page invented would otherwise go straight on
			string? correspondentName = null;
			string? documentTypeName = null;

			if (request.CorrespondentId is not null || request.DocumentTypeId is not null) {
				var choices = await client.GetChoicesAsync(cancellationToken);

				if (request.CorrespondentId is { } c) {
					correspondentName = choices.Correspondents.FirstOrDefault(x => x.Id == c)?.Name ?? throw new CommandException("Paperless has no such correspondent.");
				}

				if (request.DocumentTypeId is { } d) {
					documentTypeName = choices.DocumentTypes.FirstOrDefault(x => x.Id == d)?.Name ?? throw new CommandException("Paperless has no such document type.");
				}
			}

			var title = DocumentNames.CleanTitle(request.Title);
			var metadata = new PaperlessMetadata(title.Length == 0 ? null : title, created, request.CorrespondentId, request.DocumentTypeId);
			var pages = documents.PagePaths();

			await using var pdf = await Task.Run(() => PdfBuilder.Build(pages), cancellationToken);

			var taskId = await client.UploadAsync(pdf, DocumentNames.PdfFileName(title, created, time.GetLocalNow().DateTime), metadata, cancellationToken);
			var sent = new SendInfo(taskId, "consuming", null, null, time.GetUtcNow());

			if (!documents.SetSent(document.Id, sent)) {
				throw new ConflictException("The document was discarded while it was being sent. Paperless has the copy that was sent.");
			}

			log.Sent(document.Id, taskId, metadata, correspondentName, documentTypeName);
			scanner.Changed();
			Track(PollAsync(document.Id, taskId, sent.At));

			return sent;
		} finally {
			_sending.Release();
		}
	}

	private void Track(Task polling)
	{
		lock (_polling) {
			_polling.RemoveAll(t => t.IsCompleted);
			_polling.Add(polling);
		}
	}

	private async Task PollAsync(string documentId, string taskId, DateTimeOffset since)
	{
		var stopping = _stopping.Token;
		var failures = 0;

		try {
			using var timer = new PeriodicTimer(PollInterval, time);

			while (await timer.WaitForNextTickAsync(stopping)) {
				// the document was discarded, or changed, or sent again: this task isn't the one to follow any more
				if (documents.Current is not { Sent: { } current } now || now.Id != documentId || current.TaskId != taskId) {
					return;
				}

				try {
					var result = await client.GetTaskAsync(taskId, stopping);

					failures = 0;

					switch (result.State) {
						case PaperlessTaskState.Done:
							Finish(documentId, taskId, since, "done", result.DocumentId, result.Message);

							return;
						case PaperlessTaskState.Failed:
							Finish(documentId, taskId, since, "failed", null, result.Message);

							return;
					}
				} catch (PaperlessException ex) when (++failures >= MaxPollFailures) {
					Finish(documentId, taskId, since, "unknown", null, $"Couldn't ask Paperless how it went: {ex.Message}");

					return;
				} catch (PaperlessException ex) {
					logger.LogWarning("Asking Paperless about task {TaskId} failed ({Failures} in a row): {Message}", taskId, failures, ex.Message);
				}

				if (time.GetUtcNow() - since > GiveUpAfter) {
					Finish(documentId, taskId, since, "unknown", null, $"Paperless hasn't said how it went after {GiveUpAfter.TotalMinutes:0} minutes. Look in Paperless itself.");

					return;
				}
			}
		} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			// the service is stopping; the next start carries on from what is saved
		} catch (Exception ex) {
			logger.LogError(ex, "Following Paperless task {TaskId} failed.", taskId);
		}
	}

	private void Finish(string documentId, string taskId, DateTimeOffset since, string status, int? paperlessDocument, string? message)
	{
		// only if it's still the same send: the document may have been discarded or changed in the meantime
		if (documents.Current is { Sent: { } current } && current.TaskId == taskId && documents.SetSent(documentId, new SendInfo(taskId, status, paperlessDocument, message, since))) {
			scanner.Changed();
		}

		log.Consumed(documentId, taskId, status, paperlessDocument, message);
	}
}
