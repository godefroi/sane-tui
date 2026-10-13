using System.Text;
using ImageMagick;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace SaneTui;

/// <summary>
/// Shows a scanned preview and lets the user drag out the part of it to scan. The picture is drawn with half-block characters,
/// two picture pixels per cell (a foreground and a background color), which works in any terminal with 24-bit color. What's
/// outside the selection is dimmed. A plain click clears the selection.
/// </summary>
internal sealed class PreviewView : View
{
	private const double OutsideBrightness = 0.35;

	private PreviewResult? _result;
	private PreviewGeometry? _geometry;
	private Color[,]? _pixels;
	private (int Columns, int Rows) _builtFor;
	private SelectionMm? _selection;
	private (int Column, int Row)? _dragStart;
	private (int Column, int Row) _dragNow;

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

		SetNeedsDraw();
	}

	protected override bool OnDrawingContent(DrawContext? context)
	{
		var columns = Viewport.Width;
		var rows = Viewport.Height;

		if (_result is null) {
			DrawPlaceholder(columns, rows);

			return true;
		}

		// the picture is scaled to the view, so it's redone only when the view changes size
		if (_geometry is null || _pixels is null || _builtFor != (columns, rows)) {
			Rebuild(columns, rows);
		}

		if (_geometry is not { } geometry || _pixels is not { } pixels) {
			return true;
		}

		var selection = _dragStart is { } start ? geometry.SelectionFromDrag(start.Column, start.Row, _dragNow.Column, _dragNow.Row) : _selection;
		var background = GetAttributeForRole(VisualRole.Normal).Background;

		for (var row = geometry.FirstRow; row < geometry.FirstRow + geometry.Rows && row < rows; row++) {
			for (var column = geometry.FirstColumn; column < geometry.FirstColumn + geometry.Columns && column < columns; column++) {
				var top = PixelColor(geometry, pixels, selection, column, row, 0) ?? background;
				var bottom = PixelColor(geometry, pixels, selection, column, row, 1) ?? background;

				SetAttribute(new Terminal.Gui.Drawing.Attribute(top, bottom));
				AddRune(column, row, new Rune('▀')); // upper half block: the foreground is the top pixel, the background the bottom
			}
		}

		return true;
	}

	protected override bool OnMouseEvent(Mouse mouse)
	{
		if (_geometry is not { } geometry) {
			return base.OnMouseEvent(mouse);
		}

		// while the mouse is grabbed, events can come from outside the view; the geometry clamps those to the picture
		var position = mouse.Position ?? ScreenToViewport(mouse.ScreenPosition);

		if (_dragStart is null) {
			if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)) {
				_dragStart = (position.X, position.Y);
				_dragNow = _dragStart.Value;

				// keeps the drag going if the mouse leaves the pane, and delivers the button release wherever it happens
				App?.Mouse.GrabMouse(this);
				SetNeedsDraw();

				return true;
			}

			return base.OnMouseEvent(mouse);
		}

		if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased)) {
			var start = _dragStart.Value;

			_dragStart = null;
			App?.Mouse.UngrabMouse();

			_selection = geometry.SelectionFromDrag(start.Column, start.Row, position.X, position.Y);

			SelectionChanged?.Invoke(_selection);
			SetNeedsDraw();

			return true;
		}

		if (mouse.Flags.HasFlag(MouseFlags.PositionReport) || mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)) {
			_dragNow = (position.X, position.Y);
			SetNeedsDraw();

			return true;
		}

		return base.OnMouseEvent(mouse);
	}

	private void DrawPlaceholder(int columns, int rows)
	{
		SetAttribute(GetAttributeForRole(VisualRole.Normal));

		var row = rows / 2;
		var column = Math.Max(0, (columns - Placeholder.Length) / 2);

		Move(column, row);
		AddStr(Placeholder.Length > columns ? Placeholder[..Math.Max(0, columns)] : Placeholder);
	}

	// null where this half of the cell is outside the picture
	private static Color? PixelColor(PreviewGeometry geometry, Color[,] pixels, SelectionMm? selection, int column, int row, int half)
	{
		if (!geometry.TryGetPixel(column, row, half, out var x, out var y)) {
			return null;
		}

		var color = pixels[x, y];

		if (selection is null || geometry.SelectionContains(selection, x, y)) {
			return color;
		}

		return new Color((int)(color.R * OutsideBrightness), (int)(color.G * OutsideBrightness), (int)(color.B * OutsideBrightness));
	}

	// Scales the scanned image to the picture size the view needs. Magick does the reading, so 1-bit, 16-bit and gray scans are
	// handled as they are for files, and averaging while shrinking keeps text legible.
	private void Rebuild(int columns, int rows)
	{
		_builtFor = (columns, rows);

		if (_result is not { } result || columns < 1 || rows < 1) {
			_geometry = null;
			_pixels = null;

			return;
		}

		var geometry = new PreviewGeometry(columns, rows, result.Image.Width, result.Image.Height, result.Area);

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
	}
}
