using System.Diagnostics;
using ImageMagick;
using ImageMagick.Drawing;
using SaneTui.Sane;

namespace SaneTui.Tests;

public class PageCorrectionTests
{
	private const int PageWidth = 500;
	private const int PageHeight = 700;

	// a white page with dark lines of "text", laid on the scanner crooked, on a black lid
	private static SaneImage CrookedPage(double degrees = 3, int width = PageWidth, int height = PageHeight)
	{
		using var page = new MagickImage(MagickColors.White, (uint)width, (uint)height);

		// a few dark lines, as text has, since the straightening looks for the direction of edges
		for (var y = 60; y < height - 60; y += 40) {
			new Drawables().FillColor(MagickColors.Black).Rectangle(50, y, width - 50, y + 6).Draw(page);
		}

		using var lid = new MagickImage(MagickColors.Black, (uint)(width + 200), (uint)(height + 200));

		lid.Composite(page, 100, 100);
		lid.BackgroundColor = MagickColors.Black;

		if (degrees != 0) {
			lid.Rotate(degrees);
		}

		lid.ColorType = ColorType.Grayscale;
		lid.Depth = 8;

		var bytes = lid.ToByteArray(MagickFormat.Gray);

		return new SaneImage((int)lid.Width, (int)lid.Height, SanePixelFormat.Gray, 8, (int)lid.Width, bytes);
	}

	private static MagickImageInfo Written(SaneImage image, PageOptions options)
	{
		using var dir = new TempDirectory();
		var path = Path.Combine(dir.Path, "page.jpg");

		ImageWriter.WriteJpeg(image, 300, path, options: options);

		return new MagickImageInfo(path);
	}

	[Test]
	public async Task NoCorrections_LeaveTheSizeAlone()
	{
		var image = CrookedPage();
		var info = Written(image, default);

		await Assert.That((int)info.Width).IsEqualTo(image.Width);
		await Assert.That((int)info.Height).IsEqualTo(image.Height);
	}

	[Test]
	public async Task Trimming_CutsTheBackgroundAway()
	{
		var image = CrookedPage(0); // straight, so the page is exactly what's left
		var info = Written(image, new PageOptions(Trim: true));

		await Assert.That(Math.Abs((int)info.Width - PageWidth)).IsLessThanOrEqualTo(4);
		await Assert.That(Math.Abs((int)info.Height - PageHeight)).IsLessThanOrEqualTo(4);
	}

	[Test]
	public async Task TrimmingACrookedPage_LeavesWedgesOfBackground_WhichStraighteningRemoves()
	{
		var image = CrookedPage(3);
		var trimmed = Written(image, new PageOptions(Trim: true));
		var both = Written(image, new PageOptions(Deskew: true, Trim: true));

		// trimming alone can only cut to the bounding box of a tilted page, which is bigger than the page
		await Assert.That((int)trimmed.Width).IsGreaterThan(PageWidth + 15);

		// straightened first, what's left is the page, give or take the softened edge
		await Assert.That(Math.Abs((int)both.Width - PageWidth)).IsLessThanOrEqualTo(12);
		await Assert.That(Math.Abs((int)both.Height - PageHeight)).IsLessThanOrEqualTo(12);
	}

	[Test]
	public async Task APageWithNoBackgroundToTrim_IsLeftWhole()
	{
		var flat = new SaneImage(200, 300, SanePixelFormat.Gray, 8, 200, Enumerable.Repeat((byte)240, 200 * 300).ToArray());
		var info = Written(flat, new PageOptions(Trim: true));

		// ImageMagick may take a pixel or so off the edge of a flat picture; what matters is that the page isn't cut into
		await Assert.That((int)info.Width).IsGreaterThanOrEqualTo(190);
		await Assert.That((int)info.Height).IsGreaterThanOrEqualTo(290);
	}

	[Test]
	public async Task StraighteningAFullPage_FinishesInReasonableTime()
	{
		var image = CrookedPage(2, 2480, 3508); // A4 at 300 dpi
		var options = new PageOptions(Deskew: true, Trim: true);

		Written(image, options); // warm up

		var watch = Stopwatch.StartNew();

		Written(image, options);

		watch.Stop();

		Console.WriteLine($"Deskew and trim of an A4 page at 300 dpi: {watch.ElapsedMilliseconds} ms");

		await Assert.That(watch.ElapsedMilliseconds).IsLessThan(20_000);
	}
}
