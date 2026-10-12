using SaneTui.Sane;

namespace SaneTui.Tests;

// The test scanner is one shared device, so anything that opens it can't overlap with other tests that do.
[NotInParallel("test-backend")]
public class PreviewScanTests
{
	private const SaneCapabilities Settable = SaneCapabilities.SoftSelect | SaneCapabilities.SoftDetect;

	private static SaneOption Text(string name, string[] choices, SaneCapabilities caps = Settable) =>
		new(1, name, name, "", SaneValueType.String, SaneUnit.None, 16, caps, choices[0], null, null, choices);

	private static SaneOption Dpi(SaneRange? range = null, double[]? list = null) =>
		new(2, "resolution", "Resolution", "", SaneValueType.Int, SaneUnit.Dpi, 4, Settable, 300, range, list, null);

	private static SaneOption Switch(string name, SaneCapabilities caps = Settable) =>
		new(3, name, name, "", SaneValueType.Bool, SaneUnit.None, 4, caps, false, null, null, null);

	[Test]
	public async Task AFlatbedLikeTheCanon_GetsColor_TheLowestResolution_AndBothSpeedUps()
	{
		var options = new[] { Text("mode", ["Lineart", "Gray", "Color"]), Dpi(range: new SaneRange(50, 1200, 0)), Switch("preview"), Switch("calibration-cache") };

		var request = PreviewScan.BuildRequest("plustek:libusb:001:008", null, options);

		await Assert.That(request.Mode).IsEqualTo("Color");
		await Assert.That(request.Resolution).IsEqualTo(50d);
		await Assert.That(request.Settings).IsEquivalentTo(["preview=true", "calibration-cache=true"]);
		await Assert.That(request.Width).IsNull(); // the whole bed
		await Assert.That(request.Batch).IsFalse();
	}

	[Test]
	public async Task AScannerWithoutTheSpeedUps_IsNotAskedForThem()
	{
		var options = new[] { Text("mode", ["Lineart", "Gray", "Color"]), Dpi(list: [75, 100, 150, 200, 300]) };

		var request = PreviewScan.BuildRequest("epsonds:libusb:002:004", null, options);

		await Assert.That(request.Settings).IsEmpty(); // setting an option the scanner doesn't have would fail the preview
		await Assert.That(request.Resolution).IsEqualTo(75d);
	}

	[Test]
	public async Task SpeedUpsThatAreInactiveOrNotSettable_AreSkipped()
	{
		var options = new[] { Dpi(), Switch("preview", Settable | SaneCapabilities.Inactive), Switch("calibration-cache", SaneCapabilities.HardSelect | SaneCapabilities.SoftDetect) };

		await Assert.That(PreviewScan.BuildRequest("d", null, options).Settings).IsEmpty();
	}

	[Test]
	public async Task NoColorMode_LeavesTheModeAlone()
	{
		var options = new[] { Text("mode", ["Lineart", "Gray"]), Dpi() };

		await Assert.That(PreviewScan.BuildRequest("d", null, options).Mode).IsNull();
	}

	[Test]
	public async Task ColorIsMatchedWhateverItsSpelling()
	{
		var options = new[] { Text("mode", ["gray", "color"]), Dpi() };

		await Assert.That(PreviewScan.BuildRequest("d", null, options).Mode).IsEqualTo("color"); // the driver's own spelling
	}

	[Test]
	public async Task TheChosenSource_IsPassedOn()
	{
		await Assert.That(PreviewScan.BuildRequest("d", "Flatbed", [Dpi()]).Source).IsEqualTo("Flatbed");
	}

	[Test]
	public async Task ScanningTheTestDevice_GivesTheWholeBedAsAColorImage()
	{
		if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("libsane.so.1", out var handle)) {
			Skip.Test("libsane isn't installed here; run these in the container (see tests/Dockerfile).");
		}

		System.Runtime.InteropServices.NativeLibrary.Free(handle);

		await using var sane = await SaneLibrary.InitializeAsync();

		var reports = new List<double>();
		var result = await PreviewScan.RunAsync(sane, "test", null, new Collector(reports), CancellationToken.None);

		// the test scanner's bed is 200 x 200 mm and the lowest usual resolution it allows is 50 dpi: 393 pixels across
		await Assert.That(result.Area).IsEqualTo(new SaneScanArea(0, 0, 200, 200));
		await Assert.That(result.Dpi).IsEqualTo(50d);
		await Assert.That(result.Image.Format).IsEqualTo(SanePixelFormat.Rgb);
		await Assert.That(result.Image.Width).IsBetween(392, 394);
		await Assert.That(result.Image.Height).IsBetween(392, 394);
		await Assert.That(reports.Last()).IsEqualTo(1d);

		// and it let go of the scanner, so it can be used again straight away
		await using var again = await sane.OpenAsync("test");
	}

	private sealed class Collector(List<double> values) : IProgress<double>
	{
		public void Report(double value) => values.Add(value);
	}
}
