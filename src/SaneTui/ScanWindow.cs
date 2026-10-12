using System.Collections.ObjectModel;
using System.Globalization;
using SaneTui.Sane;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SaneTui;

/// <summary>
/// The main screen: scan settings across the top, a preview area beneath them, and a short status area at the bottom.
/// The choices offered come from what the selected scanner reports it supports, so a feeder-only scanner isn't offered a flatbed.
/// </summary>
internal sealed class ScanWindow : Window
{
	private const string FullArea = "Full area";

	// the settings panel has two columns: [label, control] on the left, [label, control] on the right
	private const int LeftLabel = 1;
	private const int LeftControl = 12;
	private const int RightLabel = 42;
	private const int RightControl = 53;

	private readonly IApplication _app;
	private readonly SaneLibrary _sane;
	private readonly ObservableCollection<string> _log = [];
	private readonly string? _errorScheme = SchemeManager.SchemesToSchemeName(Schemes.Error);

	private readonly DropDownList _scanner = new() { X = LeftControl, Y = 0, Width = Dim.Fill(1), ReadOnly = true };
	private readonly Label _sourceLabel = new() { X = LeftLabel, Y = 1, Text = "Source" };
	private readonly DropDownList _source = new() { X = LeftControl, Y = 1, Width = 28, ReadOnly = true };
	private readonly Label _modeLabel = new() { X = LeftLabel, Y = 2, Text = "Mode" };
	private readonly DropDownList _mode = new() { X = LeftControl, Y = 2, Width = 28, ReadOnly = true };
	private readonly DropDownList _resolution = new() { X = RightControl, Y = 1, Width = 10, ReadOnly = true };
	private readonly DropDownList _paper = new() { X = RightControl, Y = 2, Width = 16, ReadOnly = true };
	private readonly TextField _output = new() { X = LeftControl, Y = 3, Width = Dim.Fill(1), Text = Path.Combine(Environment.CurrentDirectory, "scan.jpg") };
	private readonly Button _scan = new() { X = LeftLabel, Y = 5, Text = "_Scan" };
	private readonly Button _quit = new() { Y = 5, Text = "_Quit" };
	private readonly Button _showLog = new() { Y = 5, Text = "_Log" };
	private readonly ProgressBar _bar = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Visible = false };
	private readonly Label _earlier = new() { X = 1, Y = 1, Width = Dim.Fill(1), Text = "" };
	private readonly Label _latest = new() { X = 1, Y = 2, Width = Dim.Fill(1), Text = "" };

	private IReadOnlyList<SaneDeviceInfo> _devices = [];
	private CancellationTokenSource? _scanning;
	private bool _populating; // setting a dropdown's value from code must not look like the user choosing
	private bool _started;

	public ScanWindow(IApplication app, SaneLibrary sane)
	{
		_app = app;
		_sane = sane;

		Title = "sane-tui  (F4 opens a dropdown, Esc quits)";

		Palette.Register();

		SchemeName = Palette.Main;

		_quit.X = Pos.Right(_scan) + 2;
		_showLog.X = Pos.Right(_quit) + 2;

		// A frame draws its lines and title in its own scheme, and its contents inherit it. So the frames take the accent
		// scheme and everything inside sits on a panel with the normal one.
		FrameView settings = new() { Title = "Scan", X = 0, Y = 0, Width = Dim.Fill(), Height = 8, SchemeName = Palette.Accent };
		// a plain View can't take focus, and the controls inside it couldn't be reached with the keyboard
		View settingsBody = new() { Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = Palette.Main, CanFocus = true };

		settings.Add(settingsBody);

		settingsBody.Add(
			new Label { X = LeftLabel, Y = 0, Text = "Scanner" }, _scanner,
			_sourceLabel, _source,
			_modeLabel, _mode,
			new Label { X = RightLabel, Y = 1, Text = "Resolution" }, _resolution, new Label { X = Pos.Right(_resolution) + 1, Y = 1, Text = "dpi" },
			new Label { X = RightLabel, Y = 2, Text = "Page size" }, _paper,
			new Label { X = LeftLabel, Y = 3, Text = "Save to" }, _output,
			_scan, _quit, _showLog);

		// Fixed height at the bottom: the progress bar (only while scanning) and the last two status messages.
		View status = new() { X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill(), Height = 3 };

		status.Add(_bar, _earlier, _latest);

		// The preview will be drawn in here. Until then it says so.
		FrameView preview = new() { Title = "Preview", X = 0, Y = Pos.Bottom(settings), Width = Dim.Fill(), Height = Dim.Fill(status), SchemeName = Palette.Accent };

		// the preview sits on a darker canvas, like a light table
		View canvas = new() { Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = Palette.Canvas };

		canvas.Add(new Label { X = Pos.Center(), Y = Pos.Center(), Text = "A preview will appear here." });
		preview.Add(canvas);

		_earlier.SchemeName = Palette.Muted;

		Add(settings, preview, status);

		SetChoices(_paper, [FullArea, .. PaperSizes.Names.Select(DisplayName)], FullArea);

		_scanner.ValueChanged += (_, _) => {
			if (!_populating && SelectedDevice() is { } device) {
				_ = LoadOptionsAsync(device);
			}
		};

		_scan.Accepted += async (_, _) => await ScanOrCancelAsync();
		_showLog.Accepted += (_, _) => ShowLog();

		_quit.Accepted += (_, _) => {
			_scanning?.Cancel();
			_app.RequestStop();
		};

		// the first moment the screen is up and handling events; before that, UI updates have nowhere to go
		IsModalChanged += (_, _) => {
			if (!_started) {
				_started = true;
				_ = LoadDevicesAsync();
			}
		};
	}

	/// <summary>Completes when a scan that was running when the window closed has finished stopping.</summary>
	public Task Finished { get; private set; } = Task.CompletedTask;

	private void OnUi(Action action) => _app.Invoke(action);

	// the status area keeps the last two messages; the pop-up log keeps all of them
	private void Log(string message, bool error = false) => OnUi(() => {
		_log.Add(message);

		_earlier.Text = _latest.Text;
		_latest.Text = message;
		_latest.SchemeName = error ? _errorScheme : null;
	});

	private void ShowLog()
	{
		using Dialog dialog = new() { Title = "Log", Width = Dim.Percent(85), Height = Dim.Percent(70), SchemeName = Palette.Accent };

		// the frame takes the accent for its lines and title; the contents sit on the normal scheme
		View body = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = Palette.Main, CanFocus = true };
		ListView list = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Source = new ListWrapper<string>(_log) };
		Button close = new() { Text = "_Close" };

		close.Accepted += (_, _) => _app.RequestStop();

		body.Add(list);
		dialog.Add(body);
		dialog.AddButton(close);

		// start on the newest message, which scrolls only if there are more messages than fit
		list.SelectedItem = _log.Count - 1;

		_app.Run(dialog);
	}

	private static string DisplayName(string paper) => char.ToUpperInvariant(paper[0]) + paper[1..];

	private static string Describe(SaneDeviceInfo device) => $"{device.Vendor} {device.Model} ({device.Name})";

	private SaneDeviceInfo? SelectedDevice() => _devices.FirstOrDefault(d => Describe(d) == _scanner.Value);

	// the dropdown's source and its value have to be set together, and without the change being mistaken for the user's
	private void SetChoices(DropDownList dropDown, IEnumerable<string> choices, string? value)
	{
		var was = _populating;

		_populating = true;
		dropDown.Source = new ListWrapper<string>(new ObservableCollection<string>(choices));
		dropDown.Value = value;
		_populating = was;
	}

	private async Task LoadDevicesAsync()
	{
		Log("Looking for scanners...");

		try {
			_devices = await _sane.GetDevicesAsync();
		} catch (SaneException ex) {
			Log($"SANE couldn't list the scanners: {ex.Message}", error: true);

			return;
		}

		if (_devices.Count == 0) {
			Log("No scanners found. 'sane-tui diag' can help work out why.", error: true);

			return;
		}

		OnUi(() => SetChoices(_scanner, _devices.Select(Describe), Describe(_devices[0])));

		await LoadOptionsAsync(_devices[0]);
	}

	// A device is opened only long enough to read what it supports, so the scan itself can open it.
	private async Task LoadOptionsAsync(SaneDeviceInfo info)
	{
		Log($"Reading what the {info.Model} supports...");

		IReadOnlyList<SaneOption> options;

		try {
			await using var device = await _sane.OpenAsync(info.Name);

			options = device.Options;
		} catch (SaneException ex) {
			Log($"Couldn't open the {info.Model}: {ex.Message}", error: true);

			return;
		}

		OnUi(() => ApplyOptions(options));
	}

	private void ApplyOptions(IReadOnlyList<SaneOption> options)
	{
		SetStringChoice(options, "source", _sourceLabel, _source);
		SetStringChoice(options, "mode", _modeLabel, _mode);

		var choices = ResolutionPresets.For(options.FirstOrDefault(o => o.Name == "resolution"));

		SetChoices(_resolution, choices.Select(Format), Format(ResolutionPresets.Default(choices)));
		SetChoices(_paper, [FullArea, .. PaperSizes.Names.Select(DisplayName)], FullArea);

		Log("Ready.");
	}

	// shown only if the scanner has the option, with the values it allows
	private void SetStringChoice(IReadOnlyList<SaneOption> options, string name, Label label, DropDownList dropDown)
	{
		var option = options.FirstOrDefault(o => o.Name == name);
		var present = option?.StringList is { Count: > 0 };

		label.Visible = present;
		dropDown.Visible = present;

		if (present) {
			SetChoices(dropDown, option!.StringList!, option.Value as string ?? option.StringList![0]);
		}
	}

	private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);

	private async Task ScanOrCancelAsync()
	{
		if (_scanning is not null) {
			_scanning.Cancel();

			return;
		}

		if (SelectedDevice() is not { } device) {
			Log("Choose a scanner first.", error: true);

			return;
		}

		// everything is read here, on the UI thread, before the scan goes to the background
		var request = new ScanRequest(
			device.Name,
			_output.Text,
			_source.Visible ? _source.Value : null,
			_mode.Visible ? _mode.Value : null,
			null,
			double.Parse(_resolution.Value ?? "300", CultureInfo.InvariantCulture),
			null,
			null,
			false,
			null,
			[],
			_paper.Value is { } paper && paper != FullArea ? paper : null);

		_scanning = new CancellationTokenSource();
		_scan.Text = "_Cancel";
		_bar.Fraction = 0;
		_bar.Visible = true;

		var token = _scanning.Token;
		var progress = new Reporter(fraction => OnUi(() => _bar.Fraction = (float)fraction));

		Finished = Task.Run(async () => {
			var exitCode = await ScanCommand.RunAsync(request, new StatusWriter(line => Log(line)), token, showProgress: false, progress);

			OnUi(() => {
				_bar.Visible = false;
				_scan.Text = "_Scan";
				_scanning?.Dispose();
				_scanning = null;

				// 130 is the user cancelling, which isn't a failure to dwell on
				if (exitCode is not (0 or 130)) {
					_latest.SchemeName = _errorScheme;
				}
			});
		});

		await Finished;
	}

	// Progress<T> would post to a synchronization context; this just calls back on the scanner's thread, which OnUi then hands over
	private sealed class Reporter(Action<double> report) : IProgress<double>
	{
		public void Report(double value) => report(value);
	}
}
