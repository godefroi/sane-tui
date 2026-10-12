using SaneTui.Sane;

namespace SaneTui.Tests;

public class DeviceChoiceTests
{
	private static readonly SaneDeviceInfo[] Devices = [
		new("plustek:libusb:001:008", "Canon", "CanoScan N650U/N656U", "flatbed scanner"),
		new("epsonds:libusb:002:004", "Epson", "ES-400", "ESC/I-2"),
	];

	private static ScanRequest Request(string? source = null, params string[] settings) =>
		new(null, "o1.jpg", source, null, null, 150, null, null, false, null, settings);

	[Test]
	public async Task MoreThanOneScanner_ShowsAnExampleCommandAndTheChoices()
	{
		var message = ScanCommand.DescribeDeviceChoice(Request(), Devices);

		await Assert.That(message).Contains("more than one scanner");
		await Assert.That(message).Contains("sane-tui scan plustek:libusb:001:008 -o o1.jpg");
		await Assert.That(message).Contains("epsonds:libusb:002:004  (Epson ES-400)");
	}

	[Test]
	public async Task ScannerNameGivenAsTheSource_IsCalledOutAndTheExampleUsesIt()
	{
		var message = ScanCommand.DescribeDeviceChoice(Request(source: "epsonds:libusb:002:004"), Devices);

		await Assert.That(message).Contains("'epsonds:libusb:002:004' is the name of a scanner");
		await Assert.That(message).Contains("sane-tui scan epsonds:libusb:002:004 -o o1.jpg");
	}

	[Test]
	public async Task ScannerNameGivenInASetting_IsCalledOutToo()
	{
		var message = ScanCommand.DescribeDeviceChoice(Request(settings: "source=plustek:libusb:001:008"), Devices);

		await Assert.That(message).Contains("is the name of a scanner");
	}

	[Test]
	public async Task OrdinarySourceName_IsNotMistakenForAScanner()
	{
		var message = ScanCommand.DescribeDeviceChoice(Request(source: "Flatbed"), Devices);

		await Assert.That(message).Contains("more than one scanner");
		await Assert.That(message).DoesNotContain("is the name of a scanner");
	}
}
