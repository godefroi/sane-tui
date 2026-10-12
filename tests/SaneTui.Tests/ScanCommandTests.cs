using System.Runtime.InteropServices;
using ImageMagick;

namespace SaneTui.Tests;

// Scans SANE's virtual "test" backend through the real command code. It's one shared device, so these can't overlap.
[NotInParallel("test-backend")]
public class ScanCommandTests
{
	private const string Feeder = "automatic document feeder"; // lowercase on purpose: the command matches the driver's spelling

	private static void RequireLibsane()
	{
		if (!NativeLibrary.TryLoad("libsane.so.1", out var handle)) {
			Skip.Test("libsane isn't installed here; run these in the container (see tests/Dockerfile).");
		}

		NativeLibrary.Free(handle);
	}

	private static ScanRequest Request(string output, string? device = "test", double resolution = 20, string? source = null, string? mode = null, double? width = null, double? height = null, bool batch = false, int? maxPages = null, string? paper = null, params string[] settings) =>
		new(device, output, source, mode, null, resolution, width, height, batch, maxPages, settings, paper);

	private static async Task<(int ExitCode, string Log)> Run(ScanRequest request, bool showProgress = false)
	{
		var log = new StringWriter();
		var exitCode = await ScanCommand.RunAsync(request, log, CancellationToken.None, showProgress);

		return (exitCode, log.ToString());
	}

	[Test]
	public async Task Paper_ScansThatSize()
	{
		RequireLibsane();

		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		// A6 is 105 x 148 mm, which the 200 x 200 mm test scanner can cover
		var (exitCode, log) = await Run(Request(output, resolution: 100, paper: "a6"));

		await Assert.That(exitCode).IsEqualTo(0);

		using var image = new MagickImage(output);

		await Assert.That((int)image.Width).IsBetween(412, 416); // 413.4
		await Assert.That((int)image.Height).IsBetween(582, 585); // 582.7
		await Assert.That(log).DoesNotContain("Note:");
	}

	[Test]
	public async Task Paper_TooBigForTheScanner_ScansWhatItCanAndSaysSo()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), paper: "a4"));

		await Assert.That(exitCode).IsEqualTo(0);
		await Assert.That(log).Contains("can't cover 210 x 297 mm");
		await Assert.That(log).Contains("It will scan 200 x 200 mm");
	}

	[Test]
	public async Task Paper_UnknownName_ListsTheKnownOnes()
	{
		// needs no libsane: a bad request is refused before the scanner is touched
		var (exitCode, log) = await Run(Request("page.jpg", paper: "tabloid"));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("a4, a5, a6, letter, legal");
	}

	[Test]
	public async Task Paper_WithWidthOrHeight_IsRefused()
	{
		var (exitCode, log) = await Run(Request("page.jpg", paper: "a4", width: 100));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("not both");
	}

	[Test]
	public async Task StartLine_SaysWhatAreaAndResolutionWillBeScanned()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (_, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), resolution: 100, width: 50, height: 30));

		await Assert.That(log).Contains("Scanning with test: 50 x 30 mm at 100 dpi");
	}

	[Test]
	public async Task Progress_OnATerminal_IsDrawnAndFinishedBeforeTheSavedMessage()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), resolution: 100, settings: ["read-limit=true", "read-limit-size=1000"]), showProgress: true);

		await Assert.That(exitCode).IsEqualTo(0);
		await Assert.That(log).Contains("\rScanning: 0%");
		await Assert.That(log).Contains("\rScanning: 100%" + Environment.NewLine + "Saved");
	}

	[Test]
	public async Task Progress_WhenRedirected_LeavesNoCarriageReturnsInTheLog()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (_, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg")), showProgress: false);

		await Assert.That(log).DoesNotContain("\r");
		await Assert.That(log).DoesNotContain("%");
	}

	[Test]
	public async Task Progress_InABatch_LabelsEachPage()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), source: Feeder, batch: true, maxPages: 2), showProgress: true);

		await Assert.That(exitCode).IsEqualTo(0);
		await Assert.That(log).Contains("\rPage 1: 100%");
		await Assert.That(log).Contains("\rPage 2: 100%");
	}

	[Test]
	public async Task NonJpegOutput_IsRefusedBeforeTouchingTheScanner()
	{
		// needs no libsane: the extension is checked first
		var (exitCode, log) = await Run(Request("page.png"));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("Only JPEG");
	}

	[Test]
	public async Task OutputFolderThatDoesNotExist_IsRefusedBeforeScanning()
	{
		// needs no libsane: the folder is checked first, so a page is never scanned with nowhere to go
		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "missing", "scan.jpg")));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("doesn't exist");
		await Assert.That(log).DoesNotContain("Scanning with");
	}

	[Test]
	public async Task SinglePage_WritesAJpegOfTheRequestedAreaAndResolution()
	{
		RequireLibsane();

		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		var (exitCode, log) = await Run(Request(output, resolution: 100, width: 50, height: 30));

		await Assert.That(exitCode).IsEqualTo(0);

		using var image = new MagickImage(output);

		// 50 x 30 mm at 100 dpi is 196.9 x 118.1 pixels
		await Assert.That((int)image.Width).IsBetween(195, 199);
		await Assert.That((int)image.Height).IsBetween(117, 120);
		await Assert.That(image.Density.X).IsEqualTo(100d);
		await Assert.That(log).Contains("Saved");
	}

	[Test]
	public async Task NoAreaGiven_ScansTheWholeScanner()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, _) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), resolution: 100));

		await Assert.That(exitCode).IsEqualTo(0);

		// far bigger than the 50 x 30 mm above: the default area is not what a scanner happens to start with
		using var image = new MagickImage(Path.Combine(dir.Path, "scan.jpg"));

		await Assert.That((int)image.Width).IsGreaterThan(400);
	}

	[Test]
	public async Task Batch_WritesOneNumberedFilePerSheetUntilTheFeederIsEmpty()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, _) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), source: Feeder, batch: true));

		await Assert.That(exitCode).IsEqualTo(0);

		// the test backend's feeder holds 10 sheets
		var files = Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)!).Order().ToList();

		await Assert.That(files.Count).IsEqualTo(10);
		await Assert.That(files[0]).IsEqualTo("scan-001.jpg");
		await Assert.That(files[9]).IsEqualTo("scan-010.jpg");
	}

	[Test]
	public async Task Batch_StopsAtMaxPages()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, _) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), source: Feeder, batch: true, maxPages: 3));

		await Assert.That(exitCode).IsEqualTo(0);
		await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(3);
	}

	[Test]
	public async Task ScannerError_IsReportedAndLeavesNoFileBehind()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), settings: "read-return-value=SANE_STATUS_JAMMED"));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("Jammed");
		await Assert.That(log).DoesNotContain("saved"); // nothing was, so the message must not claim it
		await Assert.That(Directory.GetFileSystemEntries(dir.Path)).IsEmpty();
	}

	[Test]
	public async Task CancelledScan_ExitsWith130_WithoutClaimingAPageWasSaved()
	{
		RequireLibsane();

		using var dir = new TempDirectory();
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

		// at this resolution, with a delay per read, the page takes far longer than the timeout
		var request = Request(Path.Combine(dir.Path, "scan.jpg"), resolution: 300, settings: ["read-limit=true", "read-limit-size=1000", "read-delay=true", "read-delay-duration=100000"]);
		var log = new StringWriter();

		var exitCode = await ScanCommand.RunAsync(request, log, cts.Token);

		await Assert.That(exitCode).IsEqualTo(130);
		await Assert.That(log.ToString()).Contains("Cancelled");
		await Assert.That(log.ToString()).DoesNotContain("saved");
		await Assert.That(Directory.GetFileSystemEntries(dir.Path)).IsEmpty();
	}

	private sealed class ThrowingProgress : IProgress<double>
	{
		public void Report(double value) => throw new FormatException("boom");
	}

	[Test]
	public async Task UnexpectedFailure_IsReported_NotLeftToEscapeSilently()
	{
		RequireLibsane();

		using var dir = new TempDirectory();
		var log = new StringWriter();

		// a progress callback that fails stands in for any error the command doesn't specifically expect
		var exitCode = await ScanCommand.RunAsync(Request(Path.Combine(dir.Path, "scan.jpg")), log, CancellationToken.None, reporter: new ThrowingProgress());

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log.ToString()).Contains("unexpected");
		await Assert.That(log.ToString()).Contains("FormatException");
		await Assert.That(log.ToString()).Contains("boom");
		await Assert.That(Directory.GetFileSystemEntries(dir.Path)).IsEmpty();
	}

	[Test]
	public async Task UnknownOption_SaysSoAndPointsAtDump()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), settings: "nonsense=1"));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("no option 'nonsense'");
		await Assert.That(log).Contains("dump");
	}

	[Test]
	public async Task ValueTheDeviceDoesNotAllow_ListsWhatItDoes()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), mode: "purple"));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("Choose one of: Gray, Color");
	}

	[Test]
	public async Task ModeIsMatchedCaseInsensitively()
	{
		RequireLibsane();

		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		// a picture with real color: ImageMagick writes any all-gray image as a grayscale JPEG, even from RGB data
		var (exitCode, _) = await Run(Request(output, mode: "color", settings: "test-picture=Color pattern"));

		await Assert.That(exitCode).IsEqualTo(0);

		using var image = new MagickImage(output);

		await Assert.That(image.ColorSpace).IsEqualTo(ColorSpace.sRGB);
	}

	[Test]
	public async Task UnknownDevice_IsReportedAsAScannerProblem()
	{
		RequireLibsane();

		using var dir = new TempDirectory();

		var (exitCode, log) = await Run(Request(Path.Combine(dir.Path, "scan.jpg"), device: "nonexistent:device"));

		await Assert.That(exitCode).IsEqualTo(1);
		await Assert.That(log).Contains("scanner reported a problem");
	}
}
