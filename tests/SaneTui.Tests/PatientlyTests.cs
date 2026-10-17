namespace SaneTui.Tests;

public class PatientlyTests
{
	[Test]
	public async Task SlowStep_SaysWhatItIsWaitingFor()
	{
		var inner = new StringWriter();
		var log = TextWriter.Synchronized(inner);

		// The step lasts until the notice has appeared (or ten seconds have gone), however slowly this machine runs the timer: the
		// test is that a slow step is announced, not how fast a loaded CI runner gets to it.
		await ScanCommand.Patiently("Closing...", async () => {
			for (var i = 0; i < 1000 && !Said(log, inner); i++) {
				await Task.Delay(10);
			}
		}, log, after: TimeSpan.FromMilliseconds(50));

		await Assert.That(inner.ToString()).Contains("Closing...");
	}

	// the notice is written from another thread, so reading is done under the same lock the synchronized writer uses
	private static bool Said(TextWriter log, StringWriter inner)
	{
		lock (log) {
			return inner.ToString().Contains("Closing...");
		}
	}

	[Test]
	public async Task QuickStep_StaysQuiet()
	{
		var log = new StringWriter();

		await ScanCommand.Patiently("Closing...", () => ValueTask.CompletedTask, log, after: TimeSpan.FromSeconds(30));

		await Assert.That(log.ToString()).IsEmpty();
	}

	[Test]
	public async Task FailingStep_StillThrowsAndStopsTheTimer()
	{
		var log = new StringWriter();

		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await ScanCommand.Patiently("Closing...", () => throw new InvalidOperationException("boom"), log, after: TimeSpan.FromMilliseconds(50)));

		// give a stray timer time to misfire
		await Task.Delay(150);

		await Assert.That(log.ToString()).IsEmpty();
	}
}
