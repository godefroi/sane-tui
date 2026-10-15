using System.Collections.ObjectModel;
using System.Globalization;
using ImageMagick;
using SaneTui.Sane;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
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
	private const string SelectionChoice = "Selection"; // offered in Page size while an area is dragged out on the preview
	private const string PreviewTitle = "Preview";
	private const int BedView = -1; // which view is showing: the bed preview, or the index of a scanned page

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
	private readonly CheckBox _allPages = new() { X = LeftControl, Y = 4, Text = "_All pages in the feeder (numbered files)", Visible = false };
	private readonly Button _scan = new() { X = LeftLabel, Y = 5, Text = "_Scan" };
	private readonly Button _preview = new() { Y = 5, Text = "_Preview", Visible = false }; // flatbeds only; see UpdateForSource
	private readonly Button _quit = new() { Y = 5, Text = "_Quit" };
	private readonly Button _showLog = new() { Y = 5, Text = "_Log" };
	private readonly CheckBox _rotate = new() { Y = 5, Text = "_Rotate 180°" };
	private readonly ProgressBar _bar = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Visible = false };
	private readonly Label _earlier = new() { X = 1, Y = 1, Width = Dim.Fill(1), Text = "" };
	private readonly Label _latest = new() { X = 1, Y = 2, Width = Dim.Fill(1), Text = "" };

	private readonly PreviewView _previewView = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = Palette.Canvas };
	private readonly FrameView _previewFrame = new() { Title = PreviewTitle, X = 0, Width = Dim.Fill(), SchemeName = Palette.Accent, CanFocus = true };

	private IReadOnlyList<SaneDeviceInfo> _devices = [];
	private SelectionMm? _selection; // the part of the bed dragged out on the preview, in millimeters
	private PreviewResult? _bed; // the latest bed preview, kept so it can be shown again after looking at scanned pages
	private readonly List<string> _pages = []; // files written by the latest scan, oldest first; cleared when the next one starts
	private int _viewing = BedView;
	private CancellationTokenSource? _scanning; // set while a scan or a preview runs; the one busy state they share
	private bool _populating; // setting a dropdown's value from code must not look like the user choosing
	private bool _started;

	public ScanWindow(IApplication app, SaneLibrary sane)
	{
		_app = app;
		_sane = sane;

		Title = "sane-tui  (Space opens a dropdown, Esc quits)";

		Palette.Register();

		SchemeName = Palette.Main;

		_preview.X = Pos.Right(_scan) + 2;
		_quit.X = Pos.Right(_preview) + 2;
		_showLog.X = Pos.Right(_quit) + 2;
		_rotate.X = Pos.Right(_showLog) + 3;

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
			_allPages,
			_scan, _preview, _quit, _showLog, _rotate);

		// Fixed height at the bottom: the progress bar (only while scanning) and the last two status messages.
		View status = new() { X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill(), Height = 3 };

		status.Add(_bar, _earlier, _latest);

		// The scanned preview is drawn in here, on a darker canvas like a light table. The frame has to be focusable for the picture
		// inside it to be, which the arrow keys need. (An empty focusable frame once trapped the keyboard, but the picture is a
		// focus target of its own, so Tab moves on from it.)
		_previewFrame.Y = Pos.Bottom(settings);
		_previewFrame.Height = Dim.Fill(status);
		_previewFrame.Add(_previewView);

		_earlier.SchemeName = Palette.Muted;

		Add(settings, _previewFrame, status);

		SetPaperChoices(FullArea);

		_scanner.ValueChanged += (_, _) => {
			if (!_populating && SelectedDevice() is { } device) {
				InvalidatePreview(); // a different scanner has a different bed

				_ = LoadOptionsAsync(device);
			}
		};

		_source.ValueChanged += (_, _) => {
			if (!_populating) {
				InvalidatePreview(); // a different source can have a different bed

				UpdateForSource();
			}
		};

		_paper.ValueChanged += (_, _) => {
			// choosing anything but the selection gives the selection up, so the dropdown never claims an area the scan won't use
			if (!_populating && _selection is not null && _paper.Value != SelectionChoice) {
				_selection = null;
				_previewView.Selection = null;
				UpdateTitle();

				SetPaperChoices(_paper.Value ?? FullArea);
			}
		};

		_previewView.SelectionChanged += selection => {
			_selection = selection;
			UpdateTitle();

			SetPaperChoices(selection is null ? FullArea : SelectionChoice);
		};

		// the preview shows the page the way up it'll be saved, so the selection on it is turned back to the scanner's view at scan time
		_rotate.ValueChanged += (_, _) => {
			// while a page is showing the view isn't drawing the bed, so the selection has to be turned over here
			if (_viewing != BedView && _selection is { } selection && _bed is { } bed) {
				_selection = selection.Mirrored(bed.Area);
			}

			_previewView.Rotated = Rotated;
			UpdateTitle();
		};

		_preview.Accepted += async (_, _) => await PreviewOrCancelAsync();

		// say it when the box is unticked, which is when the user is looking, not only in the log of the scan that follows
		_allPages.ValueChanged += (_, _) => {
			if (_allPages.Visible && _allPages.Value == CheckState.UnChecked) {
				Log(ScanCommand.FeederNote);
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

	private bool Rotated => _rotate.Value == CheckState.Checked;

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
		body.Add(list);
		dialog.Add(body);
		dialog.AddButton(new Button { Text = "_Close" }); // the dialog closes itself when a button is pressed

		// start on the newest message, which scrolls only if there are more messages than fit
		list.SelectedItem = _log.Count - 1;

		_app.Run(dialog);
	}

	/// <summary>Asks what to do about files that are in the way. Null means don't scan. Cancel has the focus, so a stray Enter can't replace anything.</summary>
	private ExistingFiles? AskAboutExisting(IReadOnlyList<string> existing, ScanRequest request)
	{
		var newName = Path.GetFileName(OutputFiles.FreeName(request.Output, request.Batch));

		using Dialog dialog = new() { Title = "Already exists", Width = Dim.Percent(70), Height = 11, SchemeName = Palette.Accent };

		View body = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = Palette.Main, CanFocus = true };

		body.Add(new Label {
			X = 1,
			Y = 1,
			Text = $"{OutputFiles.Describe(existing, request.Batch)}\n\nYes:     replace the existing file(s).\nNo:      keep them, and save as {newName}.\nCancel:  don't scan.",
		});

		Button cancel = new() { Text = "_Cancel" };

		dialog.Add(body);
		dialog.AddButton(new Button { Text = "_Yes" });
		dialog.AddButton(new Button { Text = "_No" });
		dialog.AddButton(cancel);

		cancel.SetFocus();

		_app.Run(dialog);

		// The dialog handles its buttons itself: it closes and records which one was pressed, by position, in Result.
		// It does not raise the buttons' own Accepted events, so handlers on them never run. Null means it was closed without a button.
		return dialog.Result switch {
			0 => ExistingFiles.Overwrite,
			1 => ExistingFiles.Rename,
			_ => null,
		};
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

		InvalidatePreview();
		UpdateForSource();

		Log("Ready.");
	}

	// A feeder feeds sheets by itself, so stopping after one can use up the next. For a feeder the sensible thing is to scan
	// everything in it, so that's the default; on a flatbed the choice doesn't exist, and a batch there would never end.
	// A preview is of a bed you can lay something on, so it's for flatbeds only.
	private void UpdateForSource()
	{
		var feeder = _source.Visible && FeederSources.Looks(_source.Value);

		_allPages.Visible = feeder;
		_allPages.Value = feeder ? CheckState.Checked : CheckState.UnChecked;
		_preview.Visible = !feeder;
	}

	// the Page size choices: full area, the paper sizes, and "Selection" while an area has been dragged out on the preview
	private void SetPaperChoices(string selected) =>
		SetChoices(_paper, [FullArea, .. _selection is null ? [] : new[] { SelectionChoice }, .. PaperSizes.Names.Select(DisplayName)], selected);

	// Both the picture and any selection belong to one bed: a different scanner or source makes them wrong, so they go. So do the
	// pages shown from the last scan, which came from that scanner.
	private void InvalidatePreview(bool keepPages = false)
	{
		_selection = null;
		_bed = null;

		if (!keepPages) {
			_pages.Clear();
		}

		_viewing = BedView;
		_previewView.SetPreview(null);
		UpdateTitle();

		SetPaperChoices(FullArea);
	}

	// What the preview pane shows is the bed preview (BedView) or one of the pages of the latest scan, by index. The bed preview and
	// its selection are kept here, not only in the view, so they're still there after paging through scanned pages and back.
	private void ShowView(int index)
	{
		_viewing = index;

		if (index == BedView) {
			_previewView.SetPreview(_bed);
			_previewView.Selection = _selection;
		} else {
			_previewView.ShowPage(_pages[index]);
		}

		UpdateTitle();
	}

	// a page just written: add it to the list and show it, so the pages appear as the scanner delivers them
	private void AddPage(string path)
	{
		_pages.Add(path);

		try {
			ShowView(_pages.Count - 1);
		} catch (Exception ex) when (ex is MagickException or IOException) {
			// the scan itself worked and the file is saved; being unable to draw it is only worth a note
			Log($"Couldn't show {Path.GetFileName(path)}: {ex.Message}", error: true);
		}
	}

	// PageUp and PageDown step through the bed preview (if there is one) and the pages scanned since Scan was last pressed
	private bool StepView(int direction)
	{
		var first = _bed is null ? 0 : BedView;
		var next = Math.Clamp(_viewing + direction, first, _pages.Count - 1);

		if (_pages.Count == 0 || next == _viewing) {
			return false;
		}

		ShowView(next);

		return true;
	}

	protected override bool OnKeyDown(Key key)
	{
		if (key == Key.PageUp || key == Key.PageDown) {
			return StepView(key == Key.PageDown ? 1 : -1) || base.OnKeyDown(key);
		}

		return base.OnKeyDown(key);
	}

	private void UpdateTitle()
	{
		_previewFrame.Title = _viewing != BedView && _viewing < _pages.Count
			? $"Page {_viewing + 1} of {_pages.Count}: {Path.GetFileName(_pages[_viewing])}  (PageUp and PageDown for the others)"
			: _selection is { } s
				? $"{PreviewTitle}  (selected {s.Width:0} x {s.Height:0} mm, {s.Left:0} mm across and {s.Top:0} mm down)"
				: _pages.Count > 0 ? $"{PreviewTitle}  (PageDown for the {_pages.Count} scanned page(s))" : PreviewTitle;
	}

	// shown only if the scanner has the option, with the values it allows
	private void SetStringChoice(IReadOnlyList<SaneOption> options, string name, Label label, DropDownList dropDown)
	{
		var offered = StringChoices.For(options.FirstOrDefault(o => o.Name == name));

		label.Visible = offered is not null;
		dropDown.Visible = offered is not null;

		if (offered is var (choices, selected)) {
			SetChoices(dropDown, choices, selected);
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

		// the area dragged out on the preview, if that's what Page size says to use
		var selection = _paper.Value == SelectionChoice ? _selection : null;

		// the selection is in what the preview shows; the scanner counts from its own corner, so a turned picture is turned back
		if (selection is not null && Rotated && _bed is { } bed) {
			selection = selection.Mirrored(bed.Area);
		}

		// everything is read here, on the UI thread, before the scan goes to the background
		var request = new ScanRequest(
			device.Name,
			_output.Text,
			_source.Visible ? _source.Value : null,
			_mode.Visible ? _mode.Value : null,
			null,
			double.Parse(_resolution.Value ?? "300", CultureInfo.InvariantCulture),
			selection?.Width,
			selection?.Height,
			_allPages.Visible && _allPages.Value == CheckState.Checked,
			null,
			[],
			_paper.Value is { } paper && paper != FullArea && paper != SelectionChoice ? paper : null,
			ExistingFiles.Fail,
			selection?.Left ?? 0,
			selection?.Top ?? 0,
			Rotated);

		// Asked before anything starts, while it costs nothing: a sheet already pulled into the scanner can't be put back.
		if (OutputFiles.Existing(request.Output, request.Batch) is { Count: > 0 } existing) {
			if (AskAboutExisting(existing, request) is not { } choice) {
				Log($"Not scanned. {OutputFiles.Describe(existing, request.Batch)}");

				return;
			}

			request = request with { IfExists = choice };
		}

		// pages from an earlier scan are no longer wanted: several rounds of scanning shouldn't pile up in one list
		_pages.Clear();
		ShowView(BedView);

		_scanning = new CancellationTokenSource();
		SetBusy(true, preview: false);
		_bar.Fraction = 0;
		_bar.Visible = true;

		var token = _scanning.Token;
		var progress = new Reporter(fraction => OnUi(() => _bar.Fraction = (float)fraction));

		Finished = Task.Run(async () => {
			var exitCode = 1;

			try {
				exitCode = await ScanCommand.RunAsync(request, new StatusWriter(line => Log(line)), token, showProgress: false, progress, pageSaved: path => OnUi(() => AddPage(path)));
			} catch (Exception ex) {
				// ScanCommand reports what it expects to go wrong; this is for what it doesn't, so the screen never just goes quiet
				Log($"Something unexpected went wrong ({ex.GetType().Name}): {ex.Message}", error: true);
			} finally {
				// always back to idle, or the Scan button would be stuck acting as Cancel
				OnUi(() => {
					_bar.Visible = false;
					SetBusy(false, preview: false);
					_scanning?.Dispose();
					_scanning = null;

					// 130 is the user cancelling, which isn't a failure to dwell on
					if (exitCode is not (0 or 130)) {
						_latest.SchemeName = _errorScheme;
					}
				});
			}
		});

		await Finished;
	}

	// A scan and a preview share the scanner and the progress bar, so only one runs at a time: its button turns into Cancel
	// and the other is disabled.
	private void SetBusy(bool busy, bool preview)
	{
		_scan.Text = busy && !preview ? "_Cancel" : "_Scan";
		_preview.Text = busy && preview ? "_Cancel" : "_Preview";
		_scan.Enabled = !busy || !preview;
		_preview.Enabled = !busy || preview;
	}

	private async Task PreviewOrCancelAsync()
	{
		if (_scanning is not null) {
			_scanning.Cancel();

			return;
		}

		if (SelectedDevice() is not { } device) {
			Log("Choose a scanner first.", error: true);

			return;
		}

		var source = _source.Visible ? _source.Value : null;

		_scanning = new CancellationTokenSource();
		SetBusy(true, preview: true);
		_bar.Fraction = 0;
		_bar.Visible = true;

		var token = _scanning.Token;
		var progress = new Reporter(fraction => OnUi(() => _bar.Fraction = (float)fraction));

		Log($"Previewing the whole bed of the {device.Model}. Many scanners calibrate before the page starts.");

		Finished = Task.Run(async () => {
			PreviewResult? result = null;

			try {
				result = await PreviewScan.RunAsync(_sane, device.Name, source, progress, token);
			} catch (OperationCanceledException) {
				Log("Preview cancelled.");
			} catch (Exception ex) when (ex is SaneException or CommandException or InvalidOperationException) {
				Log($"The preview failed: {ex.Message}", error: true);
			} catch (Exception ex) {
				Log($"Something unexpected went wrong ({ex.GetType().Name}): {ex.Message}", error: true);
			} finally {
				OnUi(() => {
					_bar.Visible = false;
					SetBusy(false, preview: true);
					_scanning?.Dispose();
					_scanning = null;

					if (result is not null) {
						// a new picture starts with no selection and the full area chosen; pages already scanned stay, they are still wanted
						InvalidatePreview(keepPages: true);
						_bed = result;
						ShowView(BedView);

						// the size the scanner actually sent, since a driver's preview mode can give less than was asked for
						Log($"Preview ready: {result.Area.Width:0.#} x {result.Area.Height:0.#} mm, scanned as {result.Image.Width} x {result.Image.Height} pixels{(result.Dpi is { } d ? $" (asked for {d:G} dpi)" : "")}. Drag over it to choose the area to scan; click to clear. Then the arrow keys slide the area by 1 mm and Shift+arrows resize it.");
						Log(_previewView.DescribeRendering());
					}
				});
			}
		});

		await Finished;
	}

	// Progress<T> would post to a synchronization context; this just calls back on the scanner's thread, which OnUi then hands over
	private sealed class Reporter(Action<double> report) : IProgress<double>
	{
		public void Report(double value) => report(value);
	}
}
