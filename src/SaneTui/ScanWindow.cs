using System.Collections.ObjectModel;
using System.Globalization;
using SaneTui.Sane;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SaneTui;

/// <summary>
/// The main screen: pick a scanner, choose how to scan, press Scan. The choices offered come from what the selected scanner
/// reports it supports, so a feeder-only scanner isn't offered a flatbed.
/// </summary>
internal sealed class ScanWindow : Window
{
	private const string FullArea = "Full area";
	private const int ControlsAt = 14;

	private readonly IApplication _app;
	private readonly SaneLibrary _sane;
	private readonly ObservableCollection<string> _log = [];

	private readonly DropDownList _scanner = new() { X = ControlsAt, Y = 1, Width = Dim.Fill(2), ReadOnly = true };
	private readonly Label _sourceLabel = new() { X = 1, Y = 3, Text = "Source" };
	private readonly DropDownList _source = new() { X = ControlsAt, Y = 3, Width = 34, ReadOnly = true };
	private readonly Label _modeLabel = new() { X = 1, Y = 4, Text = "Mode" };
	private readonly DropDownList _mode = new() { X = ControlsAt, Y = 4, Width = 34, ReadOnly = true };
	private readonly DropDownList _resolution = new() { X = ControlsAt, Y = 5, Width = 12, ReadOnly = true };
	private readonly DropDownList _paper = new() { X = ControlsAt, Y = 6, Width = 20, ReadOnly = true };
	private readonly TextField _output = new() { X = ControlsAt, Y = 8, Width = Dim.Fill(2), Text = Path.Combine(Environment.CurrentDirectory, "scan.jpg") };
	private readonly Button _scan = new() { X = 1, Y = 10, Text = "_Scan", IsDefault = true };
	private readonly Button _quit = new() { Y = 10, Text = "_Quit" };
	private readonly ProgressBar _bar = new() { X = 1, Y = 12, Width = Dim.Fill(2), Height = 1 };
	private readonly ListView _logView = new() { X = 1, Y = 14, Width = Dim.Fill(2), Height = Dim.Fill(1) };

	private IReadOnlyList<SaneDeviceInfo> _devices = [];
	private CancellationTokenSource? _scanning;
	private bool _populating; // setting a dropdown's value from code must not look like the user choosing
	private bool _started;

	public ScanWindow(IApplication app, SaneLibrary sane)
	{
		_app = app;
		_sane = sane;

		Title = "sane-tui (Esc to quit)";

		_quit.X = Pos.Right(_scan) + 2;
		_logView.Source = new ListWrapper<string>(_log);

		Add(
			new Label { X = 1, Y = 1, Text = "Scanner" },
			_scanner,
			_sourceLabel, _source,
			_modeLabel, _mode,
			new Label { X = 1, Y = 5, Text = "Resolution" }, _resolution, new Label { X = Pos.Right(_resolution) + 1, Y = 5, Text = "dpi" },
			new Label { X = 1, Y = 6, Text = "Page size" }, _paper,
			new Label { X = 1, Y = 8, Text = "Save to" }, _output,
			_scan, _quit, _bar, _logView);

		SetChoices(_paper, [FullArea, .. PaperSizes.Names.Select(DisplayName)], FullArea);

		_scanner.ValueChanged += (_, _) => {
			if (!_populating && SelectedDevice() is { } device) {
				_ = LoadOptionsAsync(device);
			}
		};

		_scan.Accepted += async (_, _) => await ScanOrCancelAsync();

		_quit.Accepted += (_, _) => {
			_scanning?.Cancel();
			RequestStop();
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

	private void Log(string message) => OnUi(() => {
		_log.Add(message);
		_logView.MoveEnd();
	});

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
			Log($"SANE couldn't list the scanners: {ex.Message}");

			return;
		}

		if (_devices.Count == 0) {
			Log("No scanners found. 'sane-tui diag' can help work out why.");

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
			Log($"Couldn't open the {info.Model}: {ex.Message}");

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
			Log("Choose a scanner first.");

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

		var token = _scanning.Token;
		var progress = new Reporter(fraction => OnUi(() => _bar.Fraction = (float)fraction));

		Finished = Task.Run(async () => {
			var exitCode = await ScanCommand.RunAsync(request, new StatusWriter(Log), token, showProgress: false, progress);

			OnUi(() => {
				_bar.Fraction = exitCode == 0 ? 1 : 0;
				_scan.Text = "_Scan";
				_scanning?.Dispose();
				_scanning = null;
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
