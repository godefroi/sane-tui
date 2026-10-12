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
internal sealed record ScanRequest(string? Device, string Output, string? Source, string? Mode, int? Depth, double Resolution, double? Width, double? Height, bool Batch, int? MaxPages, IReadOnlyList<string> Settings);

internal static class ScanCommand
{
	// 0 = scanned, 1 = failed (pages scanned before the failure are still saved), 130 = cancelled
	public static async Task<int> RunAsync(ScanRequest request, TextWriter log, CancellationToken cancellationToken)
	{
		var saved = 0;

		try {
			if (!ImageWriter.IsJpeg(request.Output)) {
				throw new CommandException("Only JPEG output is supported so far. Name the output file with a .jpg or .jpeg extension.");
			}

			// found out now, not after the page has been scanned and has nowhere to go
			var folder = Path.GetDirectoryName(Path.GetFullPath(request.Output))!;

			if (!Directory.Exists(folder)) {
				throw new CommandException($"The folder {folder} doesn't exist. Create it, or choose another output location.");
			}

			await using var sane = await SaneLibrary.InitializeAsync();

			var name = request.Device ?? await OnlyScannerAsync(sane, request);

			// opening by name skips the listing, which can take seconds because drivers probe for hardware
			await using var device = await sane.OpenAsync(name);

			await ApplyOptionsAsync(device, request);

			var dpi = Resolution(device);

			log.WriteLine($"Scanning with {name}...");

			if (request.Batch) {
				var pages = device.ScanBatchAsync(cancellationToken);

				await foreach (var page in pages.WithCancellation(cancellationToken)) {
					saved++;
					Save(page, dpi, BatchPath(request.Output, saved), log);

					if (request.MaxPages is { } max && saved >= max) {
						break;
					}
				}
			} else {
				saved++;
				Save(await device.ScanAsync(cancellationToken), dpi, request.Output, log);
			}

			return 0;
		} catch (OperationCanceledException) {
			log.WriteLine(saved > 0 ? $"Cancelled. {saved} page(s) were saved." : "Cancelled.");

			return 130;
		} catch (CommandException ex) {
			log.WriteLine(ex.Message);

			return 1;
		} catch (SaneException ex) {
			log.WriteLine(request.Batch && saved == 0 && ex.Status == SaneStatus.NoDocs
				? "The document feeder is empty."
				: $"The scanner reported a problem: {ex.Message}{(saved > 0 ? $" {saved} page(s) were saved before that." : "")}");

			return 1;
		} catch (InvalidOperationException ex) {
			// SaneDevice says why an option can't be set (inactive, read-only)
			log.WriteLine(ex.Message);

			return 1;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			log.WriteLine($"Couldn't write the image: {ex.Message}{(saved > 1 ? $" {saved - 1} earlier page(s) were saved." : "")}");

			return 1;
		}
	}

	private static async Task<string> OnlyScannerAsync(SaneLibrary sane, ScanRequest request)
	{
		var devices = await sane.GetDevicesAsync();

		return devices.Count switch {
			1 => devices[0].Name,
			0 => throw new CommandException("No scanners found. 'sane-tui diag' can help work out why."),
			_ => throw new CommandException(DescribeDeviceChoice(request, devices)),
		};
	}

	/// <summary>
	/// Explains that a scanner has to be chosen, with a command to copy. A scanner name given as the value of another option
	/// is the likeliest mistake (<c>--source plustek:...</c>), so that case is called out.
	/// </summary>
	internal static string DescribeDeviceChoice(ScanRequest request, IReadOnlyList<SaneDeviceInfo> devices)
	{
		var values = new[] { request.Source, request.Mode }.Concat(request.Settings.Select(s => s[(s.IndexOf('=') + 1)..]));
		var misplaced = values.FirstOrDefault(v => v is not null && devices.Any(d => d.Name == v));
		var chosen = misplaced ?? devices[0].Name;
		var message = new System.Text.StringBuilder();

		if (misplaced is not null) {
			message.AppendLine($"'{misplaced}' is the name of a scanner, but it was given as the value of an option. The scanner goes on its own, not after an option name.");
		} else {
			message.AppendLine("There's more than one scanner, so say which one to use.");
		}

		message.AppendLine("For example:");
		message.AppendLine($"  sane-tui scan {chosen} -o {request.Output}");
		message.AppendLine();
		message.AppendLine("Scanners found:");

		foreach (var d in devices) {
			message.AppendLine($"  {d.Name}  ({d.Vendor} {d.Model})");
		}

		return message.ToString().TrimEnd();
	}

	// Order matters: the source can change which modes and areas exist, and the mode and depth can change what resolutions are allowed.
	private static async Task ApplyOptionsAsync(SaneDevice device, ScanRequest request)
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

		try {
			await device.SetScanAreaAsync(request.Width, request.Height);
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
	}

	private static async Task SetAsync(SaneDevice device, string name, string text)
	{
		var option = OptionValues.Find(device, name);

		await device.SetAsync(option, OptionValues.Parse(option, text));
	}

	// the driver may have rounded the requested resolution, so ask what it settled on
	private static double? Resolution(SaneDevice device) =>
		device.Options.FirstOrDefault(o => o.Name == "resolution")?.Value is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;

	private static string BatchPath(string output, int page) =>
		Path.Combine(Path.GetDirectoryName(output) ?? "", $"{Path.GetFileNameWithoutExtension(output)}-{page:000}{Path.GetExtension(output)}");

	private static void Save(SaneImage page, double? dpi, string path, TextWriter log)
	{
		ImageWriter.WriteJpeg(page, dpi, path);

		log.WriteLine($"Saved {path} ({page.Width} x {page.Height} pixels{(dpi is { } d ? $", {d:G} dpi" : "")})");
	}
}
