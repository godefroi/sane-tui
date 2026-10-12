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
internal sealed record ScanRequest(string? Device, string Output, string? Source, string? Mode, int? Depth, double Resolution, double? Width, double? Height, bool Batch, int? MaxPages, IReadOnlyList<string> Settings, string? Paper = null);

internal static class ScanCommand
{
	// 0 = scanned, 1 = failed (pages scanned before the failure are still saved), 130 = cancelled
	// showProgress: draw a live percentage on the log, which only makes sense on a terminal
	// reporter: receives the fraction of each page (0 to 1), for a caller that draws its own progress
	public static async Task<int> RunAsync(ScanRequest request, TextWriter log, CancellationToken cancellationToken, bool showProgress = false, IProgress<double>? reporter = null)
	{
		var saved = 0;
		var progress = showProgress ? new ProgressLine(log) : null;
		IProgress<double>? pageProgress = progress ?? reporter;

		// a message must not land in the middle of a half-drawn progress line
		void Say(string message)
		{
			progress?.Finish();
			log.WriteLine(message);
		}

		try {
			if (!ImageWriter.IsJpeg(request.Output)) {
				throw new CommandException("Only JPEG output is supported so far. Name the output file with a .jpg or .jpeg extension.");
			}

			var (width, height) = RequestedSize(request);

			// found out now, not after the page has been scanned and has nowhere to go
			var folder = Path.GetDirectoryName(Path.GetFullPath(request.Output))!;

			if (!Directory.Exists(folder)) {
				throw new CommandException($"The folder {folder} doesn't exist. Create it, or choose another output location.");
			}

			var sane = await SaneLibrary.InitializeAsync();

			try {
				var name = request.Device ?? await OnlyScannerAsync(sane, request);

				// opening by name skips the listing, which can take seconds because drivers probe for hardware
				var device = await sane.OpenAsync(name);

				try {
					var area = await ApplyOptionsAsync(device, request, width, height);
					var dpi = Resolution(device);

					if (width is { } w && height is { } h && (area.Width < w - 1 || area.Height < h - 1)) {
						Say($"Note: this scanner can't cover {w:0.#} x {h:0.#} mm. It will scan {area.Width:0.#} x {area.Height:0.#} mm, so the page is cut off.");
					}

					Say($"Scanning with {name}: {area.Width:0.#} x {area.Height:0.#} mm{(dpi is { } d ? $" at {d:G} dpi" : "")}. Many scanners calibrate before the page starts.");

					if (request.Batch) {
						progress?.StartPage("Page 1:");

						await foreach (var page in device.ScanBatchAsync(pageProgress, cancellationToken).WithCancellation(cancellationToken)) {
							progress?.Finish();
							Save(page, dpi, BatchPath(request.Output, saved + 1), log);

							// counted only once the file exists: the failure messages say how many pages were kept
							saved++;

							if (request.MaxPages is { } max && saved >= max) {
								break;
							}

							progress?.StartPage($"Page {saved + 1}:");
						}
					} else {
						progress?.StartPage("Scanning:");

						var page = await device.ScanAsync(pageProgress, cancellationToken);

						progress?.Finish();
						Save(page, dpi, request.Output, log);

						saved++;
					}
				} finally {
					await Patiently("Closing the scanner. Some scanners return the scan head first. Press Ctrl+C twice to quit without waiting...", device.DisposeAsync, log);
				}
			} finally {
				await Patiently("Shutting down SANE. The scanner may still be returning the scan head and switching its lamp off. Press Ctrl+C twice to quit without waiting...", sane.DisposeAsync, log);
			}

			return 0;
		} catch (OperationCanceledException) {
			Say(saved > 0 ? $"Cancelled. {saved} page(s) were saved." : "Cancelled.");

			return 130;
		} catch (CommandException ex) {
			Say(ex.Message);

			return 1;
		} catch (SaneException ex) {
			Say(request.Batch && saved == 0 && ex.Status == SaneStatus.NoDocs
				? "The document feeder is empty."
				: $"The scanner reported a problem: {ex.Message}{(saved > 0 ? $" {saved} page(s) were saved before that." : "")}");

			return 1;
		} catch (InvalidOperationException ex) {
			// SaneDevice says why an option can't be set (inactive, read-only)
			Say(ex.Message);

			return 1;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Say($"Couldn't write the image: {ex.Message}{(saved > 0 ? $" {saved} earlier page(s) were saved." : "")}");

			return 1;
		} catch (Exception ex) {
			// anything not expected above (a codec failure, an odd reply from a scanner): say so, rather than ending the run with a stack trace or silence
			Say($"Something unexpected went wrong ({ex.GetType().Name}): {ex.Message}{(saved > 0 ? $" {saved} page(s) were saved before that." : "")}");

			return 1;
		}
	}

	// A paper size is a shortcut for a width and height, so it can't be combined with either.
	private static (double? Width, double? Height) RequestedSize(ScanRequest request)
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

	// Closing a scanner can take a while (it may park the scan head first). Say what we're waiting for, but only if it's slow,
	// so a quick shutdown stays quiet and a slow one doesn't look like a hang.
	internal static async Task Patiently(string message, Func<ValueTask> action, TextWriter log, TimeSpan? after = null)
	{
		using var done = new CancellationTokenSource();

		var notice = Task.Delay(after ?? TimeSpan.FromSeconds(2), done.Token).ContinueWith(t => {
			if (!t.IsCanceled) {
				log.WriteLine(message);
			}
		}, TaskScheduler.Default);

		try {
			await action();
		} finally {
			await done.CancelAsync();
			await notice;
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
	private static async Task<SaneScanArea> ApplyOptionsAsync(SaneDevice device, ScanRequest request, double? width, double? height)
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
			area = await device.SetScanAreaAsync(width, height);
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
