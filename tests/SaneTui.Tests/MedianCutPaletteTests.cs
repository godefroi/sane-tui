using System.Diagnostics;
using Terminal.Gui.Drawing;

namespace SaneTui.Tests;

public class MedianCutPaletteTests
{
	private static List<Color> Gradient(int width, int height)
	{
		var colors = new List<Color>(width * height);

		for (var y = 0; y < height; y++) {
			for (var x = 0; x < width; x++) {
				colors.Add(new Color(x * 255 / (width - 1), y * 255 / (height - 1), 128));
			}
		}

		return colors;
	}

	private static double Distance(Color a, Color b) => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

	[Test]
	public async Task NeverReturnsMoreColorsThanAsked()
	{
		var palette = new MedianCutPalette().BuildPalette(Gradient(100, 100), 64);

		await Assert.That(palette.Count).IsLessThanOrEqualTo(64);
		await Assert.That(palette.Count).IsGreaterThan(32); // and it uses them: a gradient has plenty of variety to spend them on
	}

	[Test]
	public async Task FlatColorsComeThroughExactly()
	{
		var colors = new List<Color>();

		for (var i = 0; i < 1000; i++) {
			colors.Add(new Color(255, 0, 0));
			colors.Add(new Color(0, 255, 0));
			colors.Add(new Color(0, 0, 255));
		}

		var palette = new MedianCutPalette().BuildPalette(colors, 256);

		// three colors in, three out: there is nothing to split beyond that, and averaging identical colors changes nothing
		await Assert.That(palette).IsEquivalentTo([new Color(255, 0, 0), new Color(0, 255, 0), new Color(0, 0, 255)]);
	}

	[Test]
	public async Task EveryColorOfASmoothGradient_HasAClosePaletteEntry()
	{
		var colors = Gradient(120, 120);
		var palette = new MedianCutPalette().BuildPalette(colors, 256);

		var worst = colors.Where((_, i) => i % 7 == 0).Max(c => palette.Min(p => Distance(c, p)));
		var mean = colors.Where((_, i) => i % 7 == 0).Average(c => palette.Min(p => Distance(c, p)));

		// 256 colors over a plane of 14,400 distinct ones: every color has a neighbour within a small distance
		await Assert.That(mean).IsLessThan(8d);
		await Assert.That(worst).IsLessThan(25d);
	}

	[Test]
	public async Task EmptyInput_OrNoColorsWanted_GivesNothing()
	{
		await Assert.That(new MedianCutPalette().BuildPalette([], 256)).IsEmpty();
		await Assert.That(new MedianCutPalette().BuildPalette([new Color(1, 2, 3)], 0)).IsEmpty();
	}

	[Test]
	public async Task OneColorWanted_GivesTheAverage()
	{
		var palette = new MedianCutPalette().BuildPalette([new Color(0, 0, 0), new Color(100, 200, 50)], 1);

		await Assert.That(palette).IsEquivalentTo([new Color(50, 100, 25)]);
	}

	[Test]
	public async Task EncodingAPreviewSizedPictureAsSixel_TakesWellUnderASecond()
	{
		var pixels = new Color[480, 640];

		for (var y = 0; y < 640; y++) {
			for (var x = 0; x < 480; x++) {
				pixels[x, y] = new Color(x * 255 / 479, y * 255 / 639, 128);
			}
		}

		var all = new List<Color>();

		foreach (var c in pixels) {
			all.Add(c);
		}

		// the palette is chosen once for the picture, then reused for every encode of it
		var fixedPalette = new FixedPalette(new MedianCutPalette().BuildPalette(all, 256));
		var encoder = new SixelEncoder { Quantizer = { MaxColors = 256, PaletteBuildingAlgorithm = fixedPalette } };

		encoder.EncodeSixel(pixels); // warm up

		var watch = Stopwatch.StartNew();
		var sixel = encoder.EncodeSixel(pixels);

		watch.Stop();

		// every drag step encodes the picture again, so this is the cost of a step: it has to stay a fraction of a second
		await Assert.That(sixel).StartsWith("\u001bP");
		await Assert.That(watch.ElapsedMilliseconds).IsLessThan(1000); // about a tenth of a second where this was measured
	}

	[Test]
	public async Task APreviewSizedPicture_IsQuickEnoughToRedoWhileDragging()
	{
		var colors = Gradient(480, 640); // a typical preview: about 300,000 pixels
		var palette = new MedianCutPalette();

		palette.BuildPalette(colors, 256); // warm up the JIT, so the timing is of the work and not of compiling it

		var watch = Stopwatch.StartNew();

		palette.BuildPalette(colors, 256);

		watch.Stop();

		await Assert.That(watch.ElapsedMilliseconds).IsLessThan(500);
	}
}
