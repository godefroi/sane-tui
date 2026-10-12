using System.Runtime.CompilerServices;
using static SaneTui.Sane.SaneLibrary;

namespace SaneTui.Sane;

public sealed partial class SaneDevice
{
	private int _scanning;

	/// <summary>
	/// Scans one page using the current options, and returns it as raw pixels.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Options can't be changed while this runs. Cancelling the token interrupts the scan even while it's waiting on the scanner
	/// hardware, which can take a while to stop.
	/// </para>
	/// <para>
	/// With a document feeder, prefer <see cref="ScanBatchAsync"/>. A feeder feeds sheets automatically once a job starts, so
	/// ending the job after one page can use up the next sheet without it ever being scanned.
	/// </para>
	/// </remarks>
	/// <param name="progress">Receives how much of the page has arrived, from 0 to 1. See <see cref="ScanBatchAsync"/> for when it reports.</param>
	/// <param name="cancellationToken">Cancels the scan.</param>
	/// <exception cref="OperationCanceledException">The scan was cancelled. The device stays usable.</exception>
	/// <exception cref="SaneException">The scanner reported a problem. Check <see cref="SaneException.Status"/>: for example <see cref="SaneStatus.NoDocs"/> (feeder empty), <see cref="SaneStatus.Jammed"/>, <see cref="SaneStatus.CoverOpen"/>.</exception>
	/// <exception cref="InvalidOperationException">Another scan is already in progress on this device.</exception>
	public async Task<SaneImage> ScanAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
	{
		BeginScan();

		try {
			return await _library.Worker.RunAsync(() => {
				var handle = Handle();

				try {
					return ReadPage(handle, progress, cancellationToken);
				} finally {
					Native.sane_cancel(handle);
				}
			}).ConfigureAwait(false);
		} finally {
			Volatile.Write(ref _scanning, 0);
		}
	}

	/// <summary>
	/// Scans every sheet in a document feeder, producing each page as soon as it's scanned.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The sequence ends when the feeder runs out of paper. Use this with a document feeder only: a flatbed never runs out,
	/// so on a flatbed this would scan forever. Which source is selected is an option (usually named <c>source</c>), and the
	/// names of its values vary between drivers, so this can't tell the two apart for you.
	/// </para>
	/// <para>
	/// Options can't be changed until the enumeration ends. Stopping early (<c>break</c>, or disposing the enumerator) ends the batch cleanly,
	/// but a feeder feeds sheets on its own, so it may already have pulled the next one, which is then not scanned.
	/// </para>
	/// </remarks>
	/// <param name="progress">
	/// Receives how much of the current page has arrived, from 0 to 1, and starts again at 0 for each page. It's called on a background thread, so it
	/// shouldn't do anything slow. Scanners usually spend time warming up and calibrating before the first byte arrives, so don't read
	/// a long wait at 0 as a stall. Nothing is reported while arriving if the scanner doesn't say how long the page is (hand scanners), but 1 is
	/// always reported when a page finishes. Three-pass scanners, which send red, green and blue separately, count each color from 0 to 1.
	/// </param>
	/// <param name="cancellationToken">Cancels the batch, including a page that is mid-scan.</param>
	/// <exception cref="OperationCanceledException">The batch was cancelled. The device stays usable.</exception>
	/// <exception cref="SaneException">The feeder was already empty when the batch started (<see cref="SaneStatus.NoDocs"/>), or the scanner reported a problem such as a jam. Pages already received aren't lost.</exception>
	/// <exception cref="InvalidOperationException">Another scan is already in progress on this device.</exception>
	public async IAsyncEnumerable<SaneImage> ScanBatchAsync(IProgress<double>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		BeginScan();

		try {
			for (var page = 0; ; page++) {
				SaneImage? image = null;

				try {
					image = await _library.Worker.RunAsync(() => ReadPage(Handle(), progress, cancellationToken)).ConfigureAwait(false);
				} catch (SaneException ex) when (page > 0 && ex.Status == SaneStatus.NoDocs) {
					// the feeder is empty; that's how a batch ends
				}

				if (image is null) {
					yield break;
				}

				yield return image;
			}
		} finally {
			// the batch ends with one sane_cancel, whether it finished, failed or was abandoned
			if (_handle != nint.Zero && !_library.Worker.IsDisposed) {
				var handle = _handle;

				await _library.Worker.RunAsync(() => Native.sane_cancel(handle)).ConfigureAwait(false);
			}

			Volatile.Write(ref _scanning, 0);
		}
	}

	private void BeginScan()
	{
		if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) {
			throw new InvalidOperationException("A scan is already in progress on this device.");
		}
	}

	// One page is one work item: SANE doesn't allow other calls on the handle mid-frame anyway.
	// Leaves the device scanning; callers end it with sane_cancel.
	private static SaneImage ReadPage(nint handle, IProgress<double>? progress, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Called straight from the token's thread, not queued: the worker is blocked in sane_read, so a queued cancel would never run.
		// Disposing the registration waits for a running callback, so it can't land on a closed handle.
		using var registration = cancellationToken.Register(() => Native.sane_cancel(handle));

		var frames = new List<Frame>(1);

		while (true) {
			CheckScan(Native.sane_start(handle), cancellationToken);

			// only trustworthy after sane_start; before it the values are estimates
			CheckScan(Native.sane_get_parameters(handle, out var p), cancellationToken);

			frames.Add(ReadFrame(handle, p, progress, cancellationToken));

			if (p.LastFrame != 0) {
				break;
			}
		}

		return Assemble(frames);
	}

	private static unsafe Frame ReadFrame(nint handle, NativeParameters p, IProgress<double>? progress, CancellationToken cancellationToken)
	{
		if (p.BytesPerLine <= 0 || p.PixelsPerLine <= 0) {
			throw new InvalidOperationException($"The scanner reported an unusable frame ({p.PixelsPerLine} pixels, {p.BytesPerLine} bytes per line).");
		}

		// Lines is only a hint: it's -1 for hand scanners, and a feeder with auto-crop can end early. EOF decides the height.
		// The spare byte lets the final read see EOF without growing the buffer.
		var capacity = p.Lines > 0 ? (long)p.Lines * p.BytesPerLine + 1 : (long)p.BytesPerLine * 256;

		if (capacity > Array.MaxLength) {
			throw new InvalidOperationException($"A {p.PixelsPerLine} x {p.Lines} frame is too large to hold in memory.");
		}

		var buffer = new byte[capacity];
		var used = 0;

		// one report per whole percent: more would only make the callback busy for no visible change
		var expected = p.Lines > 0 ? (long)p.Lines * p.BytesPerLine : 0;
		var lastPercent = -1;

		void Report(int percent)
		{
			if (percent != lastPercent) {
				lastPercent = percent;
				progress!.Report(percent / 100.0);
			}
		}

		if (progress is not null && expected > 0) {
			Report(0);
		}

		while (true) {
			if (used == buffer.Length) {
				if (buffer.Length == Array.MaxLength) {
					throw new InvalidOperationException("The scanner sent more data than fits in memory.");
				}

				Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
			}

			SaneStatus status;
			int length;

			fixed (byte* b = &buffer[used]) {
				status = Native.sane_read(handle, b, Math.Min(buffer.Length - used, 1 << 20), out length);
			}

			used += length;

			if (status == SaneStatus.Eof) {
				// Some backends end a cancelled read with EOF instead of "cancelled". Without this check the caller
				// would get a silently truncated page.
				cancellationToken.ThrowIfCancellationRequested();

				break;
			}

			CheckScan(status, cancellationToken);

			if (progress is not null && expected > 0) {
				Report((int)Math.Min(99, used * 100L / expected)); // 100 is only for a finished page
			}
		}

		progress?.Report(1); // a page that ends early (auto-crop) or has no announced length still finishes at 100%

		var height = used / p.BytesPerLine; // a trailing partial line is dropped

		if (height == 0) {
			throw new InvalidOperationException("The scanner sent no image data.");
		}

		return new Frame(p.Format, p.PixelsPerLine, height, p.BytesPerLine, p.Depth, buffer);
	}

	private static SaneImage Assemble(List<Frame> frames)
	{
		if (frames is [{ Format: SaneFrameFormat.Gray or SaneFrameFormat.Rgb } f]) {
			return new SaneImage(f.Width, f.Height, f.Format == SaneFrameFormat.Gray ? SanePixelFormat.Gray : SanePixelFormat.Rgb, f.Depth, f.Stride, f.Buffer.AsMemory(0, f.Stride * f.Height));
		}

		// Three-pass scanners send red, green and blue as separate frames, in a backend-chosen order; go by each frame's format.
		var red = frames.SingleOrDefault(x => x.Format == SaneFrameFormat.Red);
		var green = frames.SingleOrDefault(x => x.Format == SaneFrameFormat.Green);
		var blue = frames.SingleOrDefault(x => x.Format == SaneFrameFormat.Blue);

		if (frames.Count != 3 || red.Buffer is null || green.Buffer is null || blue.Buffer is null) {
			throw new NotSupportedException($"Unexpected frame sequence: {string.Join(", ", frames.Select(x => x.Format))}.");
		}

		if (red.Depth is not (8 or 16)) {
			throw new NotSupportedException($"Three-pass color at {red.Depth} bits per sample isn't supported.");
		}

		var width = Math.Min(red.Width, Math.Min(green.Width, blue.Width));
		var height = Math.Min(red.Height, Math.Min(green.Height, blue.Height));
		var size = red.Depth / 8;
		var stride = width * 3 * size;
		var data = new byte[stride * height];

		for (var channel = 0; channel < 3; channel++) {
			var source = channel switch { 0 => red, 1 => green, _ => blue };

			for (var y = 0; y < height; y++) {
				var from = source.Buffer.AsSpan(y * source.Stride, width * size);
				var to = data.AsSpan(y * stride);

				for (var x = 0; x < width; x++) {
					from.Slice(x * size, size).CopyTo(to.Slice(x * 3 * size + channel * size, size));
				}
			}
		}

		return new SaneImage(width, height, SanePixelFormat.Rgb, red.Depth, stride, data);
	}

	// A cancelled status means our own sane_cancel when the token fired; anything else is the backend's doing.
	private static void CheckScan(SaneStatus status, CancellationToken cancellationToken)
	{
		if (status == SaneStatus.Cancelled && cancellationToken.IsCancellationRequested) {
			throw new OperationCanceledException(cancellationToken);
		}

		Check(status);
	}

	private readonly record struct Frame(SaneFrameFormat Format, int Width, int Height, int Stride, int Depth, byte[] Buffer);
}
