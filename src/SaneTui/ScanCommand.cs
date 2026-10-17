using System.Globalization;
using SaneTui.Sane;

namespace SaneTui;

internal static class ScanCommand
{
	// short enough to fit one line of an 80-column terminal's status area
	internal const string FeederNote = "Note: a feeder feeds sheets by itself; stopping after one can use up the next.";

	// 0 = scanned, 1 = failed (pages scanned before the failure are still saved), 130 = cancelled
	// showProgress: draw a live percentage on the log, which only makes sense on a terminal
	// reporter: receives the fraction of each page (0 to 1), for a caller that draws its own progress
	// pageSaved: receives the path of each page file as soon as it's written, for a caller that shows pages as they come in
	public static async Task<int> RunAsync(ScanRequest request, TextWriter log, CancellationToken cancellationToken, bool showProgress = false, IProgress<double>? reporter = null, Action<string>? pageSaved = null)
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

			var (width, height) = ScanSetup.RequestedSize(request);

			// found out now, not after the page has been scanned and has nowhere to go
			var folder = Path.GetDirectoryName(Path.GetFullPath(request.Output))!;

			if (!Directory.Exists(folder)) {
				throw new CommandException($"The folder {folder} doesn't exist. Create it, or choose another output location.");
			}

			var output = request.Output;

			// Decided before the scanner is touched: refusing afterwards would mean a sheet had already been used up.
			if (OutputFiles.Existing(output, request.Batch) is { Count: > 0 } existing) {
				switch (request.IfExists) {
					case ExistingFiles.Fail:
						throw new CommandException($"{OutputFiles.Describe(existing, request.Batch)} Choose another name, or use --if-exists overwrite to replace it, or --if-exists rename to keep both.");
					case ExistingFiles.Rename:
						output = OutputFiles.FreeName(output, request.Batch);
						Say($"{OutputFiles.Describe(existing, request.Batch)} Saving as {Path.GetFileName(output)} instead.");
						break;
				}
			}

			var overwrite = request.IfExists == ExistingFiles.Overwrite;
			var sane = await SaneLibrary.InitializeAsync();

			try {
				var name = request.Device ?? await OnlyScannerAsync(sane, request);

				// opening by name skips the listing, which can take seconds because drivers probe for hardware
				var device = await sane.OpenAsync(name);

				try {
					var area = await ScanSetup.ApplyOptionsAsync(device, request, width, height);
					var dpi = ScanSetup.Resolution(device);

					if (width is { } w && height is { } h && (area.Width < w - 1 || area.Height < h - 1)) {
						Say($"Note: this scanner can't cover {w:0.#} x {h:0.#} mm. It will scan {area.Width:0.#} x {area.Height:0.#} mm, so the page is cut off.");
					}

					// An automatic feeder keeps feeding sheets once a job starts, so stopping after one can use up the next,
					// and it looks like the scanner skipped a page. Say so.
					if (!request.Batch && FeederSources.Looks(device.Options.FirstOrDefault(o => o.Name == "source")?.Value as string)) {
						Say(FeederNote);
					}

					var offset = area.X > 0.5 || area.Y > 0.5 ? $", {area.X:0.#} mm from the left and {area.Y:0.#} mm from the top" : "";

					Say($"Scanning with {name}: {area.Width:0.#} x {area.Height:0.#} mm{offset}{(dpi is { } d ? $" at {d:G} dpi" : "")}. Many scanners calibrate before the page starts.");

					if (request.Batch) {
						var stoppedEarly = false;

						progress?.StartPage("Page 1:");

						await foreach (var page in device.ScanBatchAsync(pageProgress, cancellationToken).WithCancellation(cancellationToken)) {
							progress?.Finish();
							var path = Save(page, dpi, BatchPath(output, saved + 1), overwrite, request.Rotate180, log);

							pageSaved?.Invoke(path);

							// counted only once the file exists: the failure messages say how many pages were kept
							saved++;

							if (request.MaxPages is { } max && saved >= max) {
								stoppedEarly = true;

								break;
							}

							progress?.StartPage($"Page {saved + 1}:");
						}

						progress?.Finish();
						Say(stoppedEarly ? $"Scanned {saved} page(s)." : $"The feeder is empty. Scanned {saved} page(s).");
					} else {
						progress?.StartPage("Scanning:");

						var page = await device.ScanAsync(pageProgress, cancellationToken);

						progress?.Finish();
						var path = Save(page, dpi, output, overwrite, request.Rotate180, log);

						pageSaved?.Invoke(path);

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

	private static string BatchPath(string output, int page) =>
		Path.Combine(Path.GetDirectoryName(output) ?? "", $"{Path.GetFileNameWithoutExtension(output)}-{page:000}{Path.GetExtension(output)}");

	private static string Save(SaneImage page, double? dpi, string path, bool overwrite, bool rotate180, TextWriter log)
	{
		var written = ImageWriter.WriteJpeg(page, dpi, path, overwrite, rotate180);

		// the up-front check already handled files that existed when the scan started; this is for one that appeared since,
		// so the page is kept under another name instead of being lost or replacing something
		if (written != path) {
			log.WriteLine($"{Path.GetFileName(path)} already exists, so this page was saved as {Path.GetFileName(written)}.");
		}

		log.WriteLine($"Saved {written} ({page.Width} x {page.Height} pixels{(dpi is { } d ? $", {d:G} dpi" : "")})");

		return written;
	}
}
