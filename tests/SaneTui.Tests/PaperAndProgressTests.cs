namespace SaneTui.Tests;

public class PaperAndProgressTests
{
	[Test]
	[Arguments("a4", 210, 297)]
	[Arguments("A4", 210, 297)]
	[Arguments("letter", 215.9, 279.4)]
	[Arguments("Legal", 215.9, 355.6)]
	[Arguments("a5", 148, 210)]
	[Arguments("a6", 105, 148)]
	public async Task PaperSize_IsLookedUpByNameIgnoringCase(string name, double width, double height)
	{
		await Assert.That(PaperSizes.TryGet(name, out var size)).IsTrue();
		await Assert.That(size.Width).IsEqualTo(width);
		await Assert.That(size.Height).IsEqualTo(height);
	}

	[Test]
	public async Task UnknownPaperSize_IsNotFound()
	{
		await Assert.That(PaperSizes.TryGet("tabloid", out _)).IsFalse();
	}

	[Test]
	public async Task ProgressLine_RedrawsInPlace_AndDrawsEachPercentOnce()
	{
		var log = new StringWriter();
		var line = new ProgressLine(log);

		line.StartPage("Scanning:");
		line.Report(0);
		line.Report(0.004); // still 0%: nothing new to draw
		line.Report(0.42);

		await Assert.That(log.ToString()).IsEqualTo("\rScanning: 0%\rScanning: 42%");
	}

	[Test]
	public async Task ProgressLine_Finish_EndsTheLineOnlyIfOneWasDrawn()
	{
		var log = new StringWriter();
		var line = new ProgressLine(log);

		line.StartPage("Page 1:");
		line.Finish(); // nothing drawn yet

		await Assert.That(log.ToString()).IsEmpty();

		line.Report(1);
		line.Finish();
		line.Finish(); // already ended

		await Assert.That(log.ToString()).IsEqualTo("\rPage 1: 100%" + Environment.NewLine);
	}

	[Test]
	public async Task ProgressLine_StartPage_BeginsANewLineForTheNextPage()
	{
		var log = new StringWriter();
		var line = new ProgressLine(log);

		line.StartPage("Page 1:");
		line.Report(1);
		line.StartPage("Page 2:");
		line.Report(0);

		await Assert.That(log.ToString()).IsEqualTo("\rPage 1: 100%" + Environment.NewLine + "\rPage 2: 0%");
	}
}
