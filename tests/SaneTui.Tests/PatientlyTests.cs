namespace SaneTui.Tests;

public class PatientlyTests
{
	[Test]
	public async Task SlowStep_SaysWhatItIsWaitingFor()
	{
		var log = new StringWriter();

		await ScanCommand.Patiently("Closing...", async () => await Task.Delay(300), log, after: TimeSpan.FromMilliseconds(50));

		await Assert.That(log.ToString()).Contains("Closing...");
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
