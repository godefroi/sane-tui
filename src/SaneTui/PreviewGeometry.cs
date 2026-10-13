using SaneTui.Sane;

namespace SaneTui;

/// <summary>A rectangle on the scanner's bed, in millimeters from its top-left corner.</summary>
internal sealed record SelectionMm(double Left, double Top, double Width, double Height);

/// <summary>
/// Where a scanned preview sits inside a view, and how view cells relate to millimeters on the scanner. Drawing and mouse
/// hit-testing both use this, so they can't disagree about where the image is.
/// </summary>
/// <remarks>
/// <para>
/// The picture covers a whole number of cells, and every cell is the same number of picture pixels wide and tall. With pixel graphics
/// (Sixel, Kitty) that is the terminal's real cell size, typically around 10 by 20 pixels. Without them it's drawn as half blocks, which
/// is one pixel wide and two tall per cell. Either way the picture is scaled to fit the view and keeps its proportions, give or take
/// the rounding to whole cells.
/// </para>
/// <para>
/// Millimeters are worked out by proportion (how far across the picture, times the width of the area that was scanned), not from the
/// resolution. Scanners round the size of what they scan, and a driver's preview mode may not honor the resolution asked for, so
/// the proportion is the reliable way to relate the picture to the bed.
/// </para>
/// </remarks>
internal sealed class PreviewGeometry
{
	private readonly SaneScanArea _bed;

	/// <param name="viewColumns">Width of the view in cells.</param>
	/// <param name="viewRows">Height of the view in cells.</param>
	/// <param name="imageWidth">Width of the preview image in pixels.</param>
	/// <param name="imageHeight">Height of the preview image in pixels.</param>
	/// <param name="bed">The area that was scanned for the preview, in millimeters. The image covers exactly this.</param>
	/// <param name="cellWidth">Picture pixels across one cell.</param>
	/// <param name="cellHeight">Picture pixels down one cell.</param>
	public PreviewGeometry(int viewColumns, int viewRows, int imageWidth, int imageHeight, SaneScanArea bed, int cellWidth = 1, int cellHeight = 2)
	{
		_bed = bed;

		CellWidth = Math.Max(1, cellWidth);
		CellHeight = Math.Max(1, cellHeight);

		var viewWidth = Math.Max(1, viewColumns);
		var viewHeight = Math.Max(1, viewRows);
		var scale = Math.Min((double)viewWidth * CellWidth / Math.Max(1, imageWidth), (double)viewHeight * CellHeight / Math.Max(1, imageHeight));

		Columns = Math.Clamp((int)Math.Round(imageWidth * scale / CellWidth), 1, viewWidth);
		Rows = Math.Clamp((int)Math.Round(imageHeight * scale / CellHeight), 1, viewHeight);
		DisplayWidth = Columns * CellWidth;
		DisplayHeight = Rows * CellHeight;
		FirstColumn = (viewWidth - Columns) / 2;
		FirstRow = (viewHeight - Rows) / 2;
	}

	/// <summary>Picture pixels across one cell.</summary>
	public int CellWidth { get; }

	/// <summary>Picture pixels down one cell.</summary>
	public int CellHeight { get; }

	/// <summary>Width of the picture as drawn, in pixels.</summary>
	public int DisplayWidth { get; }

	/// <summary>Height of the picture as drawn, in pixels.</summary>
	public int DisplayHeight { get; }

	/// <summary>The number of view columns the picture covers.</summary>
	public int Columns { get; }

	/// <summary>The number of view rows the picture covers.</summary>
	public int Rows { get; }

	/// <summary>The view column where the picture starts. It's centered, so there is margin on the sides it doesn't fill.</summary>
	public int FirstColumn { get; }

	/// <summary>The view row where the picture starts.</summary>
	public int FirstRow { get; }

	/// <summary>
	/// Which picture pixel is shown in a part of a cell: the left edge of its <paramref name="subRow"/>th row of pixels (for half blocks,
	/// 0 is the top half and 1 the bottom). False if that part of the cell is outside the picture.
	/// </summary>
	public bool TryGetPixel(int column, int row, int subRow, out int x, out int y)
	{
		x = (column - FirstColumn) * CellWidth;
		y = (row - FirstRow) * CellHeight + subRow;

		return x >= 0 && x < DisplayWidth && y >= 0 && y < DisplayHeight;
	}

	/// <summary>Where a point of the drawn picture is on the bed, in millimeters from the scanner's top-left corner.</summary>
	public (double X, double Y) ToMillimeters(double displayX, double displayY) =>
		(_bed.X + displayX / DisplayWidth * _bed.Width, _bed.Y + displayY / DisplayHeight * _bed.Height);

	/// <summary>
	/// The selection made by dragging from one cell to another, in any direction. It covers whole cells (the edges are
	/// cell edges), so it always includes everything under the two cells and never cuts off part of them. Points outside the picture
	/// count as the nearest edge. Null if both ends are the same cell: that's a click, not a drag.
	/// </summary>
	public SelectionMm? SelectionFromDrag(int column1, int row1, int column2, int row2)
	{
		var c1 = Math.Clamp(column1, FirstColumn, FirstColumn + Columns - 1);
		var c2 = Math.Clamp(column2, FirstColumn, FirstColumn + Columns - 1);
		var r1 = Math.Clamp(row1, FirstRow, FirstRow + Rows - 1);
		var r2 = Math.Clamp(row2, FirstRow, FirstRow + Rows - 1);

		if (c1 == c2 && r1 == r2) {
			return null;
		}

		var left = (Math.Min(c1, c2) - FirstColumn) * CellWidth;
		var right = (Math.Max(c1, c2) + 1 - FirstColumn) * CellWidth;
		var top = (Math.Min(r1, r2) - FirstRow) * CellHeight;
		var bottom = (Math.Max(r1, r2) + 1 - FirstRow) * CellHeight;

		var (x0, y0) = ToMillimeters(left, top);
		var (x1, y1) = ToMillimeters(right, bottom);

		return new SelectionMm(x0, y0, x1 - x0, y1 - y0);
	}

	/// <summary>Whether the middle of a picture pixel is inside the selection.</summary>
	public bool SelectionContains(SelectionMm selection, int displayX, int displayY)
	{
		var (x, y) = ToMillimeters(displayX + 0.5, displayY + 0.5);

		return x >= selection.Left && x < selection.Left + selection.Width && y >= selection.Top && y < selection.Top + selection.Height;
	}
}
