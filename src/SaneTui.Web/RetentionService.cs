using Microsoft.Extensions.Options;

namespace SaneTui.Web;

/// <summary>
/// Removes scanned documents that nobody has touched for <see cref="WebOptions.RetentionDays"/> days, so pages don't sit in the
/// data folder for ever. It sweeps when the service starts (a service that is restarted often would never reach a timer) and then
/// once a day, and stops at once when the host does.
/// </summary>
internal sealed class RetentionService(DocumentStore documents, ScannerService scanner, IOptions<WebOptions> options, TimeProvider time, ILogger<RetentionService> logger) : BackgroundService
{
	/// <summary>How long to wait between sweeps. Not a setting: a day is as often as removal by age needs to happen.</summary>
	internal TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var days = options.Value.RetentionDays;

		if (days <= 0) {
			logger.LogInformation("Scanned documents are kept until they are discarded by hand (Web__RetentionDays is {Days}).", days);

			return;
		}

		logger.LogInformation("Scanned documents are removed {Days} days after they were last changed.", days);

		using var timer = new PeriodicTimer(Interval, time);

		try {
			do {
				Sweep(TimeSpan.FromDays(days), stoppingToken);
			} while (await timer.WaitForNextTickAsync(stoppingToken));
		} catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
			// the host is stopping
		}
	}

	private void Sweep(TimeSpan age, CancellationToken stoppingToken)
	{
		// a document is being added to while a scan runs, and sweeping under it would be a race for no benefit: it's tomorrow's job
		if (scanner.IsBusy) {
			logger.LogDebug("Not sweeping old documents: the scanner is in use.");

			return;
		}

		try {
			var result = documents.RemoveOlderThan(age, time.GetUtcNow().UtcDateTime, stoppingToken);

			if (result.Removed > 0) {
				logger.LogInformation("Removed {Count} scanned document(s) not changed for {Days} days.", result.Removed, age.TotalDays);
				scanner.Changed(); // a page showing the document that went should find out
			}

			foreach (var failure in result.Failed) {
				logger.LogWarning("Couldn't remove an old document: {Failure}", failure);
			}
		} catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			// one bad sweep must not take the service down (an unhandled error in a background service stops the host)
			logger.LogError(ex, "Sweeping old documents failed; it will be tried again.");
		}
	}
}
