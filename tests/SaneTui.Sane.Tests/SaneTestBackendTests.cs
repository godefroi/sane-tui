namespace SaneTui.Sane.Tests;

// Runs against SANE's virtual "test" backend, so it needs libsane (Linux). See tests/Dockerfile.
[NotInParallel("test-backend")]
public class SaneTestBackendTests
{
	[Test]
	public async Task OpenTestDevice_ReadsOptionsWithConstraints()
	{
		await using var sane = await SaneLibrary.InitializeAsync();
		await using var device = await sane.OpenAsync("test");

		await Assert.That(sane.Version.Major).IsGreaterThanOrEqualTo(1);

		var mode = device.Options.Single(o => o.Name == "mode");
		await Assert.That(mode.Type).IsEqualTo(SaneValueType.String);
		await Assert.That(mode.Value).IsEqualTo("Gray");
		await Assert.That(mode.StringList).IsEquivalentTo(["Gray", "Color"]);

		var depth = device.Options.Single(o => o.Name == "depth");
		await Assert.That(depth.Value).IsEqualTo(8);
		await Assert.That(depth.NumberList).IsEquivalentTo([1d, 8d, 16d]);

		// the test backend's resolution is fixed-point, which also exercises the 16.16 conversion
		var resolution = device.Options.Single(o => o.Name == "resolution");
		await Assert.That(resolution.Type).IsEqualTo(SaneValueType.Fixed);
		await Assert.That(resolution.Value).IsEqualTo(50d);
		await Assert.That(resolution.Range).IsEqualTo(new SaneRange(1, 1200, 1));
		await Assert.That(resolution.Unit).IsEqualTo(SaneUnit.Dpi);

		// inactive options are listed but not read
		var threePass = device.Options.Single(o => o.Name == "three-pass");
		await Assert.That(threePass.IsActive).IsFalse();
		await Assert.That(threePass.Value).IsNull();

		var brX = device.Options.Single(o => o.Name == "br-x");
		await Assert.That(brX.Type).IsEqualTo(SaneValueType.Fixed);
		await Assert.That(brX.Value).IsTypeOf<double>();
	}

	[Test]
	public async Task GetDevices_DoesNotThrow()
	{
		await using var sane = await SaneLibrary.InitializeAsync();

		// the test backend isn't enabled in dll.conf, so the list may be empty; we only care that enumeration works
		await Assert.That(await sane.GetDevicesAsync()).IsNotNull();
	}

	[Test]
	public async Task OpenUnknownDevice_Throws()
	{
		await using var sane = await SaneLibrary.InitializeAsync();

		var ex = await Assert.ThrowsAsync<SaneException>(async () => await sane.OpenAsync("nonexistent:device"));

		await Assert.That(ex!.Status).IsNotEqualTo(SaneStatus.Good);
	}
}
