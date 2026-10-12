using SaneTui.Sane;

namespace SaneTui;

/// <param name="Image">The scanned preview.</param>
/// <param name="Area">The part of the bed the image covers, in millimeters. It's the whole bed.</param>
/// <param name="Dpi">The resolution the driver reports after setting it, if it says.</param>
internal sealed record PreviewResult(SaneImage Image, SaneScanArea Area, double? Dpi);

/// <summary>A quick, low-resolution scan of the whole bed, kept in memory, to choose the area to scan from.</summary>
internal static class PreviewScan
{
	// Options that make a scan faster at the cost of quality, if the scanner has them. "preview" is a standard option;
	// "calibration-cache" is what one driver calls reusing its calibration data, which is where a flatbed spends time before the page starts.
	private static readonly string[] SpeedOptions = ["preview", "calibration-cache"];

	/// <summary>Scans the whole bed at the scanner's lowest usual resolution. Nothing is written to disk.</summary>
	public static async Task<PreviewResult> RunAsync(SaneLibrary sane, string deviceName, string? source, IProgress<double>? progress, CancellationToken cancellationToken)
	{
		await using var device = await sane.OpenAsync(deviceName);

		var request = BuildRequest(deviceName, source, device.Options);
		var area = await ScanCommand.ApplyOptionsAsync(device, request, null, null);
		var dpi = ScanCommand.Resolution(device);
		var image = await device.ScanAsync(progress, cancellationToken);

		return new PreviewResult(image, area, dpi);
	}

	/// <summary>
	/// The settings for a preview of the scanner whose options are given. Color if it has it, since a lineart preview is no use for
	/// choosing an area; the lowest usual resolution; and the speed-ups, only where the scanner has them and allows setting them.
	/// </summary>
	internal static ScanRequest BuildRequest(string deviceName, string? source, IReadOnlyList<SaneOption> options)
	{
		var mode = options.FirstOrDefault(o => o.Name == "mode") is { IsSettable: true, StringList: { } modes }
			? modes.FirstOrDefault(m => string.Equals(m, "Color", StringComparison.OrdinalIgnoreCase))
			: null;

		var resolution = ResolutionPresets.For(options.FirstOrDefault(o => o.Name == "resolution")).Min();

		var settings = SpeedOptions
			.Where(name => options.FirstOrDefault(o => o.Name == name) is { IsSettable: true, Type: SaneValueType.Bool })
			.Select(name => $"{name}=true")
			.ToList();

		return new ScanRequest(deviceName, "", source, mode, null, resolution, null, null, false, null, settings);
	}
}
