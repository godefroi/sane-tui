using System.Text;
using ImageMagick;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Color = Terminal.Gui.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace SaneTui;

/// <summary>
/// Shows a scanned preview and lets the user drag out the part of it to scan. What's outside the selection is dimmed, and a plain
/// click clears the selection.
/// </summary>
/// <remarks>
/// <para>
/// If the terminal reports Kitty or Sixel graphics support, the picture is drawn as real pixels through an <see cref="ImageView"/>, at
/// the terminal's cell size. Otherwise it's drawn with half-block characters, two picture pixels per cell, which works in any terminal with 24-bit
/// color but is coarse: a cell is a big pixel, so a preview is only a few dozen pixels across. The view works out where the picture sits
/// (see <see cref="PreviewGeometry"/>) and handles the mouse itself in both cases.
/// </para>
/// </remarks>
internal sealed class PreviewView : View
{
	private const double OutsideBrightness = 0.35;

	private readonly Surface _surface;

	private PreviewResult? _result;
	private PreviewGeometry? _geometry;
	private Color[,]? _pixels;
	private (int Columns, int Rows, Size? Cell) _builtFor;
	private SelectionMm? _selection;
	private (int Column, int Row)? _dragStart;
	private (int Column, int Row) _dragNow;
	private SelectionMm? _shownOnSurface; // what the raster picture was last dimmed for
	private bool _surfaceCurrent;
	private Rectangle _surfaceRectangle;
	private bool _watchingDriver;

	public PreviewView()
	{
		// Encoding takes around a tenth of a second at preview size, and every drag step needs one, so it's done in the background:
		// the picture on screen keeps up with the mouse a step behind instead of the whole screen stalling.
		_surface = new Surface(this) { CanFocus = false, Visible = false, UseRasterGraphics = true, UseBackgroundRendering = true };

		Add(_surface);
	}

	/// <summary>Raised when the user finishes a drag (the new selection) or clicks (null, meaning the selection was cleared).</summary>
	public event Action<SelectionMm?>? SelectionChanged;

	public string Placeholder { get; set; } = "A preview will appear here.";

	public bool HasPreview => _result is not null;

	/// <summary>The part of the bed the user has selected, or null for none.</summary>
	public SelectionMm? Selection
	{
		get => _selection;
		set {
			_selection = value;
			RefreshSurface();
			SetNeedsDraw();
		}
	}

	/// <summary>Shows a new preview, or clears it with null. Any selection belonged to the old picture, so it goes too.</summary>
	public void SetPreview(PreviewResult? result)
	{
		_result = result;
		_pixels = null;
		_geometry = null;
		_selection = null;
		_dragStart = null;
		_surfaceCurrent = false;

		if (result is null) {
			_surface.Visible = false;
			_surface.Image = null;
		}

		SetNeedsDraw();
	}

	/// <summary>Says how the picture is being drawn, for the log: the terminal's capabilities decide how sharp it can be.</summary>
	public string DescribeRendering() => (RasterProtocol() ?? ("colored cells", null)) switch {
		(var name, { } cell) => $"Showing it with {name} ({cell.Width} x {cell.Height} pixels per cell).",
		_ => "Showing it as colored cells: this terminal hasn't reported Sixel or Kitty graphics support, so the picture is coarse.",
	};

	protected override bool OnDrawingContent(DrawContext? context)
	{
		var columns = Viewport.Width;
		var rows = Viewport.Height;

		WatchForGraphicsSupport();

		if (_result is null) {
			_surface.Visible = false;
			DrawPlaceholder(columns, rows);

			return true;
		}

		// the picture is rebuilt only when the view's size or the terminal's graphics support changes
		var cell = RasterProtocol()?.Cell;

		if (_geometry is null || _pixels is null || _builtFor != (columns, rows, cell)) {
			Rebuild(columns, rows, cell);
		}

		if (_geometry is not { } geometry || _pixels is not { } pixels) {
			return true;
		}

		var selection = LiveSelection(geometry);

		if (cell is null) {
			_surface.Visible = false;
			DrawHalfBlocks(geometry, pixels, selection, columns, rows);
		} else {
			ShowRaster(geometry, pixels, selection);
		}

		return true;
	}

	protected override bool OnMouseEvent(Mouse mouse) => HandleMouse(mouse) || base.OnMouseEvent(mouse);

	// The raster surface is a view of its own, drawn on top, so it receives the mouse where the picture is. It forwards everything here
	// instead of using its own mouse handling (which pans and zooms), so a drag means the same thing wherever it starts.
	private bool HandleMouse(Mouse mouse)
	{
		if (_geometry is not { } geometry) {
			return false;
		}

		// whichever view the event arrived at, the screen position is the same, so work from that
		var position = ScreenToViewport(mouse.ScreenPosition);

		if (_dragStart is null) {
			if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)) {
				_dragStart = (position.X, position.Y);
				_dragNow = _dragStart.Value;

				// keeps the drag going if the mouse leaves the pane, and delivers the button release wherever it happens
				App?.Mouse.GrabMouse(this);
				RefreshSurface();
				SetNeedsDraw();

				return true;
			}

			return false;
		}

		if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased)) {
			var start = _dragStart.Value;

			_dragStart = null;
			App?.Mouse.UngrabMouse();

			_selection = geometry.SelectionFromDrag(start.Column, start.Row, position.X, position.Y);

			SelectionChanged?.Invoke(_selection);
			RefreshSurface();
			SetNeedsDraw();

			return true;
		}

		if (mouse.Flags.HasFlag(MouseFlags.PositionReport) || mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)) {
			_dragNow = (position.X, position.Y);
			RefreshSurface();
			SetNeedsDraw();

			return true;
		}

		return false;
	}

	// Graphics support is detected by asking the terminal, and the answer can arrive after the first draw, so redraw when it does.
	private void WatchForGraphicsSupport()
	{
		if (_watchingDriver || App?.Driver is not { } driver) {
			return;
		}

		_watchingDriver = true;

		driver.SixelSupportChanged += (_, _) => App?.Invoke(SetNeedsDraw);
		driver.KittyGraphicsSupportChanged += (_, _) => App?.Invoke(SetNeedsDraw);
	}

	// Which pixel graphics the terminal can show, and how big its cells are. Kitty is preferred, as Terminal.Gui does.
	private (string Name, Size? Cell)? RasterProtocol()
	{
		if (App?.Driver is not { } driver) {
			return null;
		}

		if (driver.KittyGraphicsSupport is { IsSupported: true } kitty && Plausible(kitty.Resolution)) {
			return ("Kitty graphics", kitty.Resolution);
		}

		if (driver.SixelSupport is { IsSupported: true } sixel && Plausible(sixel.Resolution)) {
			return ("Sixel graphics", sixel.Resolution);
		}

		return null;
	}

	// a terminal that reports support but a nonsense cell size would produce a nonsense picture
	private static bool Plausible(Size cell) => cell.Width is >= 4 and <= 64 && cell.Height is >= 8 and <= 128;

	private void DrawPlaceholder(int columns, int rows)
	{
		SetAttribute(GetAttributeForRole(VisualRole.Normal));

		var row = rows / 2;
		var column = Math.Max(0, (columns - Placeholder.Length) / 2);

		Move(column, row);
		AddStr(Placeholder.Length > columns ? Placeholder[..Math.Max(0, columns)] : Placeholder);
	}

	private void DrawHalfBlocks(PreviewGeometry geometry, Color[,] pixels, SelectionMm? selection, int columns, int rows)
	{
		var background = GetAttributeForRole(VisualRole.Normal).Background;

		for (var row = geometry.FirstRow; row < geometry.FirstRow + geometry.Rows && row < rows; row++) {
			for (var column = geometry.FirstColumn; column < geometry.FirstColumn + geometry.Columns && column < columns; column++) {
				var top = PixelColor(geometry, pixels, selection, column, row, 0) ?? background;
				var bottom = PixelColor(geometry, pixels, selection, column, row, 1) ?? background;

				SetAttribute(new Terminal.Gui.Drawing.Attribute(top, bottom));
				AddRune(column, row, new Rune('▀')); // upper half block: the foreground is the top pixel, the background the bottom
			}
		}
	}

	// the picture is drawn by the surface; this positions it over exactly the cells the geometry says, and hands it the dimmed picture
	private void ShowRaster(PreviewGeometry geometry, Color[,] pixels, SelectionMm? selection)
	{
		var rectangle = new Rectangle(geometry.FirstColumn, geometry.FirstRow, geometry.Columns, geometry.Rows);

		if (_surfaceRectangle != rectangle) {
			_surfaceRectangle = rectangle;
			_surface.X = rectangle.X;
			_surface.Y = rectangle.Y;
			_surface.Width = rectangle.Width;
			_surface.Height = rectangle.Height;
		}

		_surface.Visible = true;

		if (!_surfaceCurrent) {
			SetSurfaceImage(geometry, pixels, selection);
		}
	}

	// Changing the picture has to happen outside the draw pass: the surface is redrawn only if it was invalidated before the pass began.
	private void RefreshSurface()
	{
		if (_geometry is not { } geometry || _pixels is not { } pixels || !_surface.Visible) {
			return;
		}

		var selection = LiveSelection(geometry);

		if (!_surfaceCurrent || _shownOnSurface != selection) {
			SetSurfaceImage(geometry, pixels, selection);
		}
	}

	private void SetSurfaceImage(PreviewGeometry geometry, Color[,] pixels, SelectionMm? selection)
	{
		_surface.Image = selection is null ? pixels : Dimmed(pixels, geometry, selection);
		_shownOnSurface = selection;
		_surfaceCurrent = true;
	}

	// what's selected right now: the drag in progress if there is one, otherwise the committed selection
	private SelectionMm? LiveSelection(PreviewGeometry geometry) =>
		_dragStart is { } start ? geometry.SelectionFromDrag(start.Column, start.Row, _dragNow.Column, _dragNow.Row) : _selection;

	// null where this part of the cell is outside the picture
	private static Color? PixelColor(PreviewGeometry geometry, Color[,] pixels, SelectionMm? selection, int column, int row, int subRow)
	{
		if (!geometry.TryGetPixel(column, row, subRow, out var x, out var y)) {
			return null;
		}

		var color = pixels[x, y];

		return selection is null || geometry.SelectionContains(selection, x, y) ? color : Dim(color);
	}

	private static Color Dim(Color color) => new((int)(color.R * OutsideBrightness), (int)(color.G * OutsideBrightness), (int)(color.B * OutsideBrightness));

	private static Color[,] Dimmed(Color[,] source, PreviewGeometry geometry, SelectionMm selection)
	{
		var width = source.GetLength(0);
		var height = source.GetLength(1);
		var result = new Color[width, height];

		for (var y = 0; y < height; y++) {
			for (var x = 0; x < width; x++) {
				result[x, y] = geometry.SelectionContains(selection, x, y) ? source[x, y] : Dim(source[x, y]);
			}
		}

		return result;
	}

	// Scales the scanned image to the picture size the view needs. Magick does the reading, so 1-bit, 16-bit and gray scans are
	// handled as they are for files, and averaging while shrinking keeps text legible.
	private void Rebuild(int columns, int rows, Size? cell)
	{
		_builtFor = (columns, rows, cell);
		_surfaceCurrent = false;

		if (_result is not { } result || columns < 1 || rows < 1) {
			_geometry = null;
			_pixels = null;

			return;
		}

		var geometry = cell is { } c
			? new PreviewGeometry(columns, rows, result.Image.Width, result.Image.Height, result.Area, c.Width, c.Height)
			: new PreviewGeometry(columns, rows, result.Image.Width, result.Image.Height, result.Area);

		using var magick = ImageWriter.ToMagick(result.Image);

		magick.Depth = 8;
		magick.Resize(new MagickGeometry((uint)geometry.DisplayWidth, (uint)geometry.DisplayHeight) { IgnoreAspectRatio = true });

		var bytes = magick.ToByteArray(MagickFormat.Rgb);
		var pixels = new Color[geometry.DisplayWidth, geometry.DisplayHeight];

		for (var y = 0; y < geometry.DisplayHeight; y++) {
			for (var x = 0; x < geometry.DisplayWidth; x++) {
				var i = (y * geometry.DisplayWidth + x) * 3;

				pixels[x, y] = new Color(bytes[i], bytes[i + 1], bytes[i + 2]);
			}
		}

		_geometry = geometry;
		_pixels = pixels;

		if (cell is not null) {
			_surface.SixelEncoder = CreateEncoder(pixels);
		}
	}

	// Terminal.Gui's default Sixel encoder uses 64 colors picked by popularity, which turns a scan's gradients into bands. This one has
	// 256, chosen by median cut over the picture and its dimmed version, and chosen once: picking a palette is much of the cost of an
	// encode, and the picture only ever changes by being dimmed.
	private static SixelEncoder CreateEncoder(Color[,] pixels)
	{
		var width = pixels.GetLength(0);
		var height = pixels.GetLength(1);
		var sample = new List<Color>(width * height / 8 + 1);

		for (var y = 0; y < height; y += 2) {
			for (var x = 0; x < width; x += 4) {
				sample.Add(pixels[x, y]);
				sample.Add(Dim(pixels[x, y]));
			}
		}

		return new SixelEncoder { Quantizer = { MaxColors = 256, PaletteBuildingAlgorithm = new FixedPalette(new MedianCutPalette().BuildPalette(sample, 256)) } };
	}

	// The picture's own view. It never takes focus and hands the mouse to its owner.
	private sealed class Surface(PreviewView owner) : ImageView
	{
		protected override bool OnMouseEvent(Mouse mouse) => owner.HandleMouse(mouse);
	}
}
