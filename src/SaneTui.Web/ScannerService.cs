using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ImageMagick;
using SaneTui.Sane;

namespace SaneTui.Web;

/// <summary>Another operation is using the scanner.</summary>
internal sealed class BusyException() : Exception("The scanner is busy. Wait for the current operation to finish, or cancel it.");

/// <summary>
/// The one scanner and what it's doing. SANE state is global to the process and a feeder can't be shared, so this allows one
/// operation at a time; a second request is refused rather than queued. Operations run in the background, independent of any
/// browser: closing a tab doesn't stop a scan. Every change is published as a <see cref="Snapshot"/> to whoever is watching.
/// </summary>
internal sealed class ScannerService(DocumentStore documents) : IAsyncDisposable
{
	private const int MaxLog = 200;
	private const int PreviewQuality = 85;

	private readonly object _lock = new();
	private readonly SemaphoreSlim _starting = new(1, 1);
	private readonly List<LogLine> _log = [];
	private readonly List<Channel<Snapshot>> _watchers = [];

	private SaneLibrary? _sane;
	private IReadOnlyList<SaneDeviceInfo>? _devices;
	private Operation? _operation;
	private double? _progress;
	private long _revision;
	private int _previewVersion;
	private PreviewInfo? _previewInfo;
	private byte[]? _previewImage;

	public Snapshot GetSnapshot()
	{
		lock (_lock) {
			return Build();
		}
	}

	public byte[]? PreviewImage
	{
		get {
			lock (_lock) {
				return _previewImage;
			}
		}
	}

	/// <summary>Snapshots as they change, starting with the current one. Intermediate ones are dropped for a slow reader: only the latest matters.</summary>
	public async IAsyncEnumerable<Snapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var channel = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

		lock (_lock) {
			_watchers.Add(channel);
			channel.Writer.TryWrite(Build());
		}

		try {
			await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken)) {
				yield return snapshot;
			}
		} finally {
			lock (_lock) {
				_watchers.Remove(channel);
			}
		}
	}

	/// <summary>Tells watchers something changed that the service didn't do itself (a page deleted, the document discarded).</summary>
	public void Changed()
	{
		lock (_lock) {
			Publish();
		}
	}

	/// <summary>The scanners SANE can see. Listing probes for hardware, so the last answer is reused unless a refresh is asked for.</summary>
	/// <exception cref="BusyException">The scanner is in use.</exception>
	public async Task<IReadOnlyList<SaneDeviceInfo>> GetDevicesAsync(bool refresh)
	{
		if (!refresh && _devices is { } known) {
			return known;
		}

		using var operation = Begin("listing");

		var sane = await GetLibraryAsync();

		return _devices = await sane.GetDevicesAsync();
	}

	/// <summary>What a scanner offers, for the page to build its choices from. The scanner is opened only long enough to read its options.</summary>
	/// <exception cref="BusyException">The scanner is in use.</exception>
	/// <param name="source">
	/// Read the options as they are with this source selected: the modes, resolutions and the scanner's own on/off options
	/// often differ between a flatbed and a feeder. Null leaves the scanner's current source.
	/// </param>
	public async Task<Capabilities> GetCapabilitiesAsync(string device, string? source = null)
	{
		using var operation = Begin("reading options");

		var sane = await GetLibraryAsync();

		await using var open = await sane.OpenAsync(device);

		if (source is not null && StringChoices.For(open.Options.FirstOrDefault(o => o.Name == "source")) is { } offered && offered.Choices.Contains(source)) {
			await open.SetAsync(open.GetOption("source"), source); // the library refreshes the options a change affects
		}

		var options = open.Options;
		var sources = StringChoices.For(options.FirstOrDefault(o => o.Name == "source"));
		var modes = StringChoices.For(options.FirstOrDefault(o => o.Name == "mode"));
		var resolutions = ResolutionPresets.For(options.FirstOrDefault(o => o.Name == "resolution"));

		// "preview" is what the Preview button turns on by itself; as a checkbox for real scans it would only be a way to spoil them
		var switches = options
			.Where(o => o is { IsSettable: true, Type: SaneValueType.Bool } && o.Name is not ("" or "preview"))
			.Select(o => new SwitchOption(o.Name, o.Title, o.Description, o.Value is true))
			.ToList();

		return new Capabilities(
			device,
			sources is var (sourceChoices, sourceSelected) ? new ChoiceSet(sourceChoices, sourceSelected) : null,
			modes is var (modeChoices, modeSelected) ? new ChoiceSet(modeChoices, modeSelected) : null,
			[.. sources?.Choices.Where(FeederSources.Looks) ?? []],
			resolutions,
			ResolutionPresets.Default(resolutions),
			[.. PaperSizes.Names],
			switches);
	}

	/// <summary>Starts a preview of the whole bed in the background.</summary>
	/// <exception cref="BusyException">The scanner is in use.</exception>
	public void StartPreview(PreviewRequest request)
	{
		var operation = Begin("previewing");

		Log("Previewing the whole bed. Many scanners calibrate before the page starts.");

		operation.Task = Task.Run(() => RunAsync(operation, "Preview", async () => {
			var sane = await GetLibraryAsync();
			var result = await PreviewScan.RunAsync(sane, request.Device, NullIfBlank(request.Source), Reporter(operation), operation.Token);

			using var magick = ImageWriter.ToMagick(result.Image);

			magick.Depth = 8;
			magick.Quality = PreviewQuality;

			var bytes = magick.ToByteArray(MagickFormat.Jpeg);

			lock (_lock) {
				_previewImage = bytes;
				_previewInfo = new PreviewInfo(++_previewVersion, result.Area, result.Image.Width, result.Image.Height, result.Dpi);
			}

			Log($"Preview ready: {result.Area.Width:0.#} x {result.Area.Height:0.#} mm, scanned as {result.Image.Width} x {result.Image.Height} pixels{(result.Dpi is { } d ? $" (asked for {d:G} dpi)" : "")}.");
		}));
	}

	/// <summary>Starts a scan in the background; each page is saved into the current document as it arrives.</summary>
	/// <exception cref="BusyException">The scanner is in use.</exception>
	/// <exception cref="CommandException">The request makes no sense, for example a paper size together with an area.</exception>
	public void StartScan(ScanJobRequest job)
	{
		var request = new ScanRequest(job.Device, "", NullIfBlank(job.Source), NullIfBlank(job.Mode), null, job.Resolution, job.Width, job.Height, job.AllPages, null, job.Settings ?? [], job.Paper, ExistingFiles.Fail, job.Left ?? 0, job.Top ?? 0, job.Rotate180, job.Deskew, job.Trim);
		var (width, height) = ScanSetup.RequestedSize(request);
		var operation = Begin("scanning");

		operation.Task = Task.Run(() => RunAsync(operation, "Scan", () => ScanAsync(operation, request, width, height)));
	}

	// A scanner that has no choice of source (or mode) has nothing to set, and a page can end up sending an empty string for it.
	// Setting an option to "" would fail, so it's taken to mean "leave it".
	private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

	public void Cancel()
	{
		lock (_lock) {
			_operation?.Cancel();
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task? running;

		lock (_lock) {
			_operation?.Cancel();
			running = _operation?.Task;
		}

		if (running is not null) {
			await running;
		}

		if (_sane is not null) {
			await _sane.DisposeAsync();
		}
	}

	private async Task ScanAsync(Operation operation, ScanRequest request, double? width, double? height)
	{
		var sane = await GetLibraryAsync();

		await using var device = await sane.OpenAsync(request.Device!);

		var area = await ScanSetup.ApplyOptionsAsync(device, request, width, height);
		var dpi = ScanSetup.Resolution(device);

		if (width is { } w && height is { } h && (area.Width < w - 1 || area.Height < h - 1)) {
			Log($"Note: this scanner can't cover {w:0.#} x {h:0.#} mm. It will scan {area.Width:0.#} x {area.Height:0.#} mm, so the page is cut off.");
		}

		// An automatic feeder keeps feeding once a job starts, so stopping after one sheet can use up the next.
		if (!request.Batch && FeederSources.Looks(device.Options.FirstOrDefault(o => o.Name == "source")?.Value as string)) {
			Log("Note: a feeder feeds sheets by itself; stopping after one can use up the next.");
		}

		var offset = area.X > 0.5 || area.Y > 0.5 ? $", {area.X:0.#} mm from the left and {area.Y:0.#} mm from the top" : "";

		Log($"Scanning with {request.Device}: {area.Width:0.#} x {area.Height:0.#} mm{offset}{(dpi is { } d ? $" at {d:G} dpi" : "")}. Many scanners calibrate before the page starts.");

		var saved = 0;

		try {
			if (request.Batch) {
				// Pages are corrected and saved on their own task, so a slow one (a deskew takes a while) doesn't keep the feeder waiting
				// between sheets. A few are allowed to queue; beyond that the scanner waits, which keeps memory bounded.
				var pending = Channel.CreateBounded<SaneImage>(new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
				var saver = Task.Run(async () => {
					try {
						await foreach (var queued in pending.Reader.ReadAllAsync()) {
							Save(queued, dpi, request.Page);
							saved++;
						}
					} catch (Exception ex) {
						pending.Writer.TryComplete(ex); // so the scanning loop isn't left waiting on a queue nobody empties

						throw;
					}
				});

				try {
					await foreach (var image in device.ScanBatchAsync(Reporter(operation), operation.Token).WithCancellation(operation.Token)) {
						await pending.Writer.WriteAsync(image, operation.Token);
					}
				} finally {
					// whatever stopped the scanning, pages already scanned are still saved; and a failure to save is the real cause of any
					// "channel closed" the loop saw, so it's what gets reported
					pending.Writer.TryComplete();

					await saver;
				}

				Log($"The feeder is empty. Scanned {saved} page(s).");
			} else {
				Save(await device.ScanAsync(Reporter(operation), operation.Token), dpi, request.Page);
			}
		} catch (SaneException ex) when (request.Batch && saved == 0 && ex.Status == SaneStatus.NoDocs) {
			Log("The document feeder is empty.", error: true);
		}
	}

	private void Save(SaneImage image, double? dpi, PageOptions options)
	{
		var page = documents.AddPage(image, dpi, options);

		Log($"Saved a page ({page.Width} x {page.Height} pixels{(dpi is { } d ? $", {d:G} dpi" : "")}).");
	}

	// Runs an operation to its end, whatever happens, and says how it ended: the scanner must never stay marked busy.
	private async Task RunAsync(Operation operation, string name, Func<Task> body)
	{
		try {
			await body();
		} catch (OperationCanceledException) {
			Log($"{name} cancelled.");
		} catch (Exception ex) when (ex is SaneException or CommandException or InvalidOperationException) {
			Log(ex.Message, error: true);
		} catch (Exception ex) {
			Log($"Something unexpected went wrong ({ex.GetType().Name}): {ex.Message}", error: true);
		} finally {
			operation.Dispose();
		}
	}

	private async Task<SaneLibrary> GetLibraryAsync()
	{
		if (_sane is { } sane) {
			return sane;
		}

		await _starting.WaitAsync();

		try {
			return _sane ??= await SaneLibrary.InitializeAsync();
		} finally {
			_starting.Release();
		}
	}

	private Operation Begin(string status)
	{
		lock (_lock) {
			if (_operation is not null) {
				throw new BusyException();
			}

			_operation = new Operation(status, this);
			_progress = null;

			Publish();

			return _operation;
		}
	}

	private void End(Operation operation)
	{
		lock (_lock) {
			if (_operation == operation) {
				_operation = null;
				_progress = null;
			}

			Publish();
		}
	}

	private void Log(string text, bool error = false)
	{
		lock (_lock) {
			_log.Add(new LogLine(DateTimeOffset.Now, text, error));

			if (_log.Count > MaxLog) {
				_log.RemoveAt(0);
			}

			Publish();
		}
	}

	private ProgressReporter Reporter(Operation operation) => new(fraction => {
		lock (_lock) {
			if (_operation == operation) {
				_progress = fraction;

				Publish();
			}
		}
	});

	// the caller holds the lock
	private void Publish()
	{
		_revision++;

		var snapshot = Build();

		foreach (var watcher in _watchers) {
			watcher.Writer.TryWrite(snapshot);
		}
	}

	// the caller holds the lock
	private Snapshot Build() => new(_revision, _operation?.Status ?? "idle", _progress, [.. _log], documents.Current, _previewInfo);

	// Progress<T> would post to a synchronization context; this calls back on the scanner's thread
	private sealed class ProgressReporter(Action<double> report) : IProgress<double>
	{
		public void Report(double value) => report(value);
	}

	private sealed class Operation(string status, ScannerService owner) : IDisposable
	{
		private readonly CancellationTokenSource _cancellation = new();

		public string Status { get; } = status;

		/// <summary>The background work, once started; read only to wait for it at shutdown.</summary>
		public Task? Task { get; set; }

		public CancellationToken Token => _cancellation.Token;

		public void Cancel() => _cancellation.Cancel();

		public void Dispose()
		{
			owner.End(this);
			_cancellation.Dispose();
		}
	}
}
