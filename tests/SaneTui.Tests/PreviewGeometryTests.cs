using SaneTui.Sane;

namespace SaneTui.Tests;

public class PreviewGeometryTests
{
	private static readonly SaneScanArea Letterish = new(0, 0, 200, 400); // a bed twice as tall as it is wide

	[Test]
	public async Task TallImage_IsFittedByHeight_AndCenteredAcrossTheView()
	{
		// 100 x 200 pixels into 40 columns x 20 rows (a 40 x 40 pixel grid): scale 0.2 gives 20 x 40
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		await Assert.That(g.DisplayWidth).IsEqualTo(20);
		await Assert.That(g.DisplayHeight).IsEqualTo(40);
		await Assert.That(g.FirstColumn).IsEqualTo(10);
		await Assert.That(g.FirstRow).IsEqualTo(0);
		await Assert.That(g.Rows).IsEqualTo(20);
	}

	[Test]
	public async Task WideImage_IsFittedByWidth_AndCenteredDownTheView()
	{
		var g = new PreviewGeometry(40, 20, 200, 100, new SaneScanArea(0, 0, 400, 200));

		await Assert.That(g.DisplayWidth).IsEqualTo(40);
		await Assert.That(g.DisplayHeight).IsEqualTo(20);
		await Assert.That(g.Rows).IsEqualTo(10);
		await Assert.That(g.FirstRow).IsEqualTo(5);
	}

	[Test]
	public async Task DraggingAcrossTheWholePicture_SelectsTheWholeBed()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		var s = g.SelectionFromDrag(10, 0, 29, 19)!;

		await Assert.That(s.Left).IsEqualTo(0d).Within(1e-9);
		await Assert.That(s.Top).IsEqualTo(0d).Within(1e-9);
		await Assert.That(s.Width).IsEqualTo(200d).Within(1e-9);
		await Assert.That(s.Height).IsEqualTo(400d).Within(1e-9);
	}

	[Test]
	public async Task DraggingOverTheLeftHalf_SelectsTheLeftHalfOfTheBed()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		// columns 10 to 19 are the left 10 of the picture's 20 columns
		var s = g.SelectionFromDrag(10, 0, 19, 19)!;

		await Assert.That(s.Left).IsEqualTo(0d).Within(1e-9);
		await Assert.That(s.Width).IsEqualTo(100d).Within(1e-9);
		await Assert.That(s.Height).IsEqualTo(400d).Within(1e-9);
	}

	[Test]
	public async Task TheDirectionOfTheDrag_DoesNotMatter()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		await Assert.That(g.SelectionFromDrag(24, 15, 12, 3)).IsEqualTo(g.SelectionFromDrag(12, 3, 24, 15));
		await Assert.That(g.SelectionFromDrag(12, 15, 24, 3)).IsEqualTo(g.SelectionFromDrag(24, 3, 12, 15));
	}

	[Test]
	public async Task Selection_AlwaysCoversWholeCells_SoNothingTheUserCoveredIsCutOff()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		var s = g.SelectionFromDrag(13, 4, 22, 11)!;

		// each cell is 10 mm wide and 20 mm tall here (200 mm over 20 columns, 400 mm over 20 rows), and the drag covers
		// columns 13 to 22 and rows 4 to 11 inclusive, edges included
		await Assert.That(s.Left).IsEqualTo(30d).Within(1e-9);
		await Assert.That(s.Width).IsEqualTo(100d).Within(1e-9);
		await Assert.That(s.Top).IsEqualTo(80d).Within(1e-9);
		await Assert.That(s.Height).IsEqualTo(160d).Within(1e-9);
	}

	[Test]
	public async Task AClick_OrAOneCellDrag_IsNotASelection()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		await Assert.That(g.SelectionFromDrag(15, 8, 15, 8)).IsNull();
	}

	[Test]
	public async Task DraggingPastTheEdge_StopsAtTheEdge()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		var s = g.SelectionFromDrag(-5, -5, 200, 200)!;

		await Assert.That(s.Left).IsEqualTo(0d).Within(1e-9);
		await Assert.That(s.Top).IsEqualTo(0d).Within(1e-9);
		await Assert.That(s.Width).IsEqualTo(200d).Within(1e-9);
		await Assert.That(s.Height).IsEqualTo(400d).Within(1e-9);
	}

	[Test]
	public async Task TheBedsOwnOffset_IsAddedToWhatIsSelected()
	{
		// the preview covers an area that starts 10 mm in and 5 mm down
		var g = new PreviewGeometry(40, 20, 100, 200, new SaneScanArea(10, 5, 200, 400));

		var s = g.SelectionFromDrag(10, 0, 19, 19)!;

		await Assert.That(s.Left).IsEqualTo(10d).Within(1e-9);
		await Assert.That(s.Top).IsEqualTo(5d).Within(1e-9);
		await Assert.That(s.Width).IsEqualTo(100d).Within(1e-9);
	}

	[Test]
	public async Task Pixels_AreFoundFromCells_AndNothingOutsideThePictureHasOne()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);

		await Assert.That(g.TryGetPixel(10, 0, 0, out var x, out var y)).IsTrue();
		await Assert.That((x, y)).IsEqualTo((0, 0));

		await Assert.That(g.TryGetPixel(29, 19, 1, out x, out y)).IsTrue();
		await Assert.That((x, y)).IsEqualTo((19, 39));

		await Assert.That(g.TryGetPixel(9, 5, 0, out _, out _)).IsFalse(); // left margin
		await Assert.That(g.TryGetPixel(30, 5, 0, out _, out _)).IsFalse(); // right margin
	}

	[Test]
	public async Task SelectionContains_JudgesPixelsByTheirMiddle()
	{
		var g = new PreviewGeometry(40, 20, 100, 200, Letterish);
		var s = g.SelectionFromDrag(10, 0, 19, 19)!; // the left half

		await Assert.That(g.SelectionContains(s, 0, 0)).IsTrue();
		await Assert.That(g.SelectionContains(s, 9, 39)).IsTrue();
		await Assert.That(g.SelectionContains(s, 10, 0)).IsFalse();
	}

	[Test]
	public async Task OddHeightPicture_KeepsTheLastRowsLowerHalfOutsideThePicture()
	{
		// 3 pixels tall: two rows, the second row's bottom half has no pixel
		var g = new PreviewGeometry(10, 5, 10, 3, new SaneScanArea(0, 0, 10, 3));

		await Assert.That(g.DisplayHeight).IsEqualTo(3);
		await Assert.That(g.Rows).IsEqualTo(2);
		await Assert.That(g.TryGetPixel(g.FirstColumn, g.FirstRow + 1, 0, out _, out _)).IsTrue();
		await Assert.That(g.TryGetPixel(g.FirstColumn, g.FirstRow + 1, 1, out _, out _)).IsFalse();

		var s = g.SelectionFromDrag(g.FirstColumn, g.FirstRow, g.FirstColumn + 1, g.FirstRow + 1)!;

		await Assert.That(s.Height).IsEqualTo(3d).Within(1e-9); // reaches the bed's edge, not past it
	}

	[Test]
	public async Task TinyOrEmptyViews_DoNotCrash_AndAPictureOfOneCellCannotBeSelectedWithin()
	{
		var g = new PreviewGeometry(0, 0, 100, 200, Letterish);

		await Assert.That(g.DisplayWidth).IsGreaterThanOrEqualTo(1);
		await Assert.That(g.DisplayHeight).IsGreaterThanOrEqualTo(1);

		// the whole picture is one cell, so every drag is a click
		await Assert.That(g.SelectionFromDrag(0, 0, 5, 5)).IsNull();
	}
}
