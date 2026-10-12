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

	private static ScanRequest Request(string output, string? device = "test", double resolution = 20, string? source = null, string? mode = null, double? width = null, double? height = null, bool batch = false, int? maxPages = null, params string[] settings) =>
		new(device, output, source, mode, null, resolution, width, height, batch, maxPages, settings);

	private static async Task<(int ExitCode, string Log)> Run(ScanRequest request)
	{
		var log = new StringWriter();
		var exitCode = await ScanCommand.RunAsync(request, log, CancellationToken.None);

		return (exitCode, log.ToString());
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
