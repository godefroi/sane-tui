namespace SaneTui.Sane.Tests;

// SANE's virtual "test" backend is one shared device (its ADF counts pages), so these can't run alongside each other.
[NotInParallel("test-backend")]
public class SaneScanTests
{
	private const string Feeder = "Automatic Document Feeder";

	private static async Task Set(SaneDevice device, string name, object value) => await device.SetAsync(device.GetOption(name), value);

	// small and quick: the test backend scales the image with the resolution
	private static async Task<(SaneLibrary, SaneDevice)> OpenAsync()
	{
		var sane = await SaneLibrary.InitializeAsync();
		var device = await sane.OpenAsync("test");

		await Set(device, "resolution", 20);

		return (sane, device);
	}

	private static async Task AssertLayout(SaneImage image, SanePixelFormat format, int depth)
	{
		var samplesPerPixel = format == SanePixelFormat.Rgb ? 3 : 1;
		var minStride = (image.Width * samplesPerPixel * depth + 7) / 8;

		await Assert.That(image.Format).IsEqualTo(format);
		await Assert.That(image.Depth).IsEqualTo(depth);
		await Assert.That(image.Width).IsGreaterThan(0);
		await Assert.That(image.Height).IsGreaterThan(0);
		await Assert.That(image.Stride).IsGreaterThanOrEqualTo(minStride);
		await Assert.That(image.Data.Length).IsEqualTo(image.Stride * image.Height);
	}

	[Test]
	public async Task SetMode_ActivatesThreePass_AndRefreshesOptions()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Assert.That(device.GetOption("three-pass").IsActive).IsFalse();

		var info = await device.SetAsync(device.GetOption("mode"), "Color");

		await Assert.That(device.GetOption("mode").Value).IsEqualTo("Color");
		await Assert.That(device.GetOption("three-pass").IsActive).IsTrue();
		await Assert.That(info.HasFlag(SaneSetInfo.ReloadOptions)).IsTrue();
	}

	[Test]
	public async Task SetFixedOption_RoundTripsThroughFixedPoint()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await device.SetAsync(device.GetOption("resolution"), 75d);

		await Assert.That(device.GetOption("resolution").Value).IsEqualTo(75d);
	}

	[Test]
	public async Task SetInactiveOption_Throws()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Assert.ThrowsAsync<InvalidOperationException>(async () => await device.SetAsync(device.GetOption("three-pass"), true));
	}

	[Test]
	public async Task SetOversizedString_Throws_InsteadOfOverrunningTheBackendBuffer()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Assert.ThrowsAsync<ArgumentException>(async () => await device.SetAsync(device.GetOption("mode"), new string('x', 100)));
	}

	[Test]
	public async Task Scan_Gray8()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await AssertLayout(await device.ScanAsync(), SanePixelFormat.Gray, 8);
	}

	[Test]
	public async Task Scan_Gray16()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "depth", 16);

		var image = await device.ScanAsync();

		await AssertLayout(image, SanePixelFormat.Gray, 16);
		await Assert.That(image.Stride).IsGreaterThanOrEqualTo(image.Width * 2);
	}

	[Test]
	public async Task Scan_Lineart1()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "depth", 1);

		await AssertLayout(await device.ScanAsync(), SanePixelFormat.Gray, 1);
	}

	[Test]
	public async Task Scan_Color_IsInterleavedRgb()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "mode", "Color");

		await AssertLayout(await device.ScanAsync(), SanePixelFormat.Rgb, 8);
	}

	[Test]
	[Arguments("RGB")]
	[Arguments("BGR")]
	public async Task Scan_ThreePass_MatchesSinglePassColor(string order)
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "mode", "Color");
		await Set(device, "test-picture", "Color pattern");

		var single = await device.ScanAsync();

		await Set(device, "three-pass", true);
		await Set(device, "three-pass-order", order);

		var threePass = await device.ScanAsync();

		await AssertLayout(threePass, SanePixelFormat.Rgb, 8);
		await Assert.That(threePass.Width).IsEqualTo(single.Width);
		await Assert.That(threePass.Height).IsEqualTo(single.Height);

		// compare pixel by pixel: the two images may differ in row padding
		for (var y = 0; y < single.Height; y++) {
			var a = single.Data.Span.Slice(y * single.Stride, single.Width * 3);
			var b = threePass.Data.Span.Slice(y * threePass.Stride, threePass.Width * 3);

			if (!a.SequenceEqual(b)) {
				Assert.Fail($"Row {y} differs between single-pass and three-pass ({order}).");
			}
		}
	}

	[Test]
	public async Task Scan_HandScanner_UnknownHeight()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "hand-scanner", true);

		await AssertLayout(await device.ScanAsync(), SanePixelFormat.Gray, 8);
	}

	[Test]
	public async Task ScanBatch_Feeder_StopsWhenEmpty()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "source", Feeder);

		var pages = 0;

		await foreach (var image in device.ScanBatchAsync()) {
			await AssertLayout(image, SanePixelFormat.Gray, 8);
			pages++;
		}

		// the test backend's feeder holds 10 sheets
		await Assert.That(pages).IsEqualTo(10);
	}

	[Test]
	public async Task Scan_BackendError_ThrowsWithStatus()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "read-return-value", "SANE_STATUS_JAMMED");

		var ex = await Assert.ThrowsAsync<SaneException>(async () => await device.ScanAsync());

		await Assert.That(ex!.Status).IsEqualTo(SaneStatus.Jammed);
	}

	[Test]
	public async Task Scan_Cancelled_ThrowsAndDeviceStaysUsable()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		// ~100 ms per read of 1000 bytes, and at this resolution there are many reads, so the page takes far longer than the timeout
		await Set(device, "resolution", 300);
		await Set(device, "read-limit", true);
		await Set(device, "read-limit-size", 1000);
		await Set(device, "read-delay", true);
		await Set(device, "read-delay-duration", 100_000);

		using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500))) {
			await Assert.ThrowsAsync<OperationCanceledException>(async () => await device.ScanAsync(cts.Token));
		}

		await Set(device, "read-delay", false);
		await Set(device, "read-limit", false);
		await Set(device, "resolution", 20);

		await AssertLayout(await device.ScanAsync(), SanePixelFormat.Gray, 8);
	}

	[Test]
	public async Task SetOption_DuringBatch_Throws()
	{
		var (sane, device) = await OpenAsync();

		await using var _ = sane;
		await using var __ = device;

		await Set(device, "source", Feeder);

		await using var batch = device.ScanBatchAsync().GetAsyncEnumerator();

		await Assert.That(await batch.MoveNextAsync()).IsTrue();
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await Set(device, "resolution", 30));
	}
}
