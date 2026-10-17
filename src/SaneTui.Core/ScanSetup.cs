using System.Globalization;
using SaneTui.Sane;

namespace SaneTui;

/// <param name="Device">The device name, or null to use the only scanner SANE finds.</param>
/// <param name="Output">The JPEG file to write. With <paramref name="Batch"/>, the page number is added to the name.</param>
/// <param name="Resolution">Dots per inch. Scanners often default to a low value, so the command picks 300 unless told otherwise.</param>
/// <param name="Width">Millimeters, or null for the full width of the scanner.</param>
/// <param name="Height">Millimeters, or null for the full height of the scanner.</param>
/// <param name="Batch">Scan every sheet in the document feeder.</param>
/// <param name="MaxPages">Stop a batch after this many pages.</param>
/// <param name="Settings">Extra <c>name=value</c> options, applied last.</param>
/// <param name="Paper">A paper size name (see <see cref="PaperSizes"/>) to use instead of <paramref name="Width"/> and <paramref name="Height"/>.</param>
/// <param name="IfExists">What to do if the output files are already there. The default refuses, so nothing is ever replaced unasked.</param>
/// <param name="Left">Millimeters from the left edge of the scanner to the left of the area. Only meaningful with a <paramref name="Width"/> and <paramref name="Height"/>.</param>
/// <param name="Top">Millimeters from the top edge of the scanner to the top of the area.</param>
internal sealed record ScanRequest(string? Device, string Output, string? Source, string? Mode, int? Depth, double Resolution, double? Width, double? Height, bool Batch, int? MaxPages, IReadOnlyList<string> Settings, string? Paper = null, ExistingFiles IfExists = ExistingFiles.Fail, double Left = 0, double Top = 0, bool Rotate180 = false);

/// <summary>Turning a <see cref="ScanRequest"/> into settings on an open scanner, shared by scanning and previewing.</summary>
internal static class ScanSetup
{
	// A paper size is a shortcut for a width and height, so it can't be combined with either.
	internal static (double? Width, double? Height) RequestedSize(ScanRequest request)
	{
		if (request.Paper is not { } paper) {
			return (request.Width, request.Height);
		}

		if (request.Width is not null || request.Height is not null) {
			throw new CommandException("Use --paper, or --width and --height, not both.");
		}

		return PaperSizes.TryGet(paper, out var size)
			? (size.Width, size.Height)
			: throw new CommandException($"'{paper}' isn't a paper size I know. Choose one of: {string.Join(", ", PaperSizes.Names)}. Or give --width and --height in millimeters.");
	}

	// Order matters: the source can change which modes and areas exist, and the mode and depth can change what resolutions are allowed.
	internal static async Task<SaneScanArea> ApplyOptionsAsync(SaneDevice device, ScanRequest request, double? width, double? height)
	{
		if (request.Source is { } source) {
			await SetAsync(device, "source", source);
		}

		if (request.Mode is { } mode) {
			await SetAsync(device, "mode", mode);
		}

		if (request.Depth is { } depth) {
			await SetAsync(device, "depth", depth.ToString(CultureInfo.InvariantCulture));
		}

		await SetAsync(device, "resolution", request.Resolution.ToString(CultureInfo.InvariantCulture));

		SaneScanArea area;

		try {
			area = await device.SetScanAreaAsync(width, height, request.Left, request.Top);
		} catch (NotSupportedException ex) {
			throw new CommandException($"Can't choose the scan area on this device: {ex.Message}");
		} catch (ArgumentOutOfRangeException ex) {
			throw new CommandException(ex.Message);
		}

		foreach (var setting in request.Settings) {
			var equals = setting.IndexOf('=');

			if (equals <= 0) {
				throw new CommandException($"'{setting}' should look like name=value.");
			}

			await SetAsync(device, setting[..equals].Trim(), setting[(equals + 1)..].Trim());
		}

		return area;
	}

	private static async Task SetAsync(SaneDevice device, string name, string text)
	{
		var option = OptionValues.Find(device, name);

		await device.SetAsync(option, OptionValues.Parse(option, text));
	}

	// the driver may have rounded the requested resolution, so ask what it settled on
	internal static double? Resolution(SaneDevice device) =>
		device.Options.FirstOrDefault(o => o.Name == "resolution")?.Value is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;
}
