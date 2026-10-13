using Terminal.Gui.Drawing;

namespace SaneTui;

/// <summary>
/// Chooses a palette for a Sixel image by median cut: the set of colors is split again and again along the channel where it spreads
/// widest, at the median, and each piece is averaged to one palette color. That spends the palette where the picture actually has
/// color variety. Terminal.Gui's built-in builder takes the most common exact colors instead, which falls apart on a scan, where
/// smooth gradients and noise mean almost every pixel is a different color.
/// </summary>
internal sealed class MedianCutPalette : IPaletteBuilder
{
	// Enough to find a good palette; more colors don't change it noticeably, and every one costs time on each redraw.
	private const int MaxSamples = 50_000;

	public List<Color> BuildPalette(List<Color> colors, int maxColors)
	{
		if (colors.Count == 0 || maxColors < 1) {
			return [];
		}

		var step = Math.Max(1, colors.Count / MaxSamples);
		var pixels = new int[(colors.Count + step - 1) / step];

		for (int i = 0, n = 0; i < colors.Count; i += step, n++) {
			pixels[n] = colors[i].R << 16 | colors[i].G << 8 | colors[i].B;
		}

		var boxes = new List<(int Start, int Length)> { (0, pixels.Length) };

		while (boxes.Count < maxColors) {
			var (index, shift) = WidestBox(pixels, boxes);

			if (index < 0) {
				break; // every box is a single color already
			}

			var (start, length) = boxes[index];

			Array.Sort(pixels, start, length, Comparer<int>.Create((a, b) => (a >> shift & 0xFF).CompareTo(b >> shift & 0xFF)));

			// Split near the middle, but where the value changes, so identical colors stay in one box. Cutting through a run of them
			// would give the same color several palette entries.
			var half = length / 2;
			var value = pixels[start + half - 1] >> shift & 0xFF;
			var split = half;

			while (split < length && (pixels[start + split] >> shift & 0xFF) == value) {
				split++;
			}

			if (split == length) {
				split = half;

				while (split > 0 && (pixels[start + split - 1] >> shift & 0xFF) == value) {
					split--;
				}
			}

			boxes[index] = (start, split);
			boxes.Add((start + split, length - split));
		}

		var palette = new List<Color>(boxes.Count);

		foreach (var (start, length) in boxes) {
			long r = 0, g = 0, b = 0;

			for (var i = start; i < start + length; i++) {
				r += pixels[i] >> 16 & 0xFF;
				g += pixels[i] >> 8 & 0xFF;
				b += pixels[i] & 0xFF;
			}

			palette.Add(new Color((int)((r + length / 2) / length), (int)((g + length / 2) / length), (int)((b + length / 2) / length)));
		}

		return palette;
	}

	// the box with the biggest spread in any one channel, and which channel (as a bit shift); -1 if no box has any spread
	private static (int Index, int Shift) WidestBox(int[] pixels, List<(int Start, int Length)> boxes)
	{
		var bestIndex = -1;
		var bestShift = 0;
		var bestSpread = 0;

		for (var index = 0; index < boxes.Count; index++) {
			var (start, length) = boxes[index];

			if (length < 2) {
				continue;
			}

			int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;

			for (var i = start; i < start + length; i++) {
				int r = pixels[i] >> 16 & 0xFF, g = pixels[i] >> 8 & 0xFF, b = pixels[i] & 0xFF;

				if (r < minR) minR = r;
				if (r > maxR) maxR = r;
				if (g < minG) minG = g;
				if (g > maxG) maxG = g;
				if (b < minB) minB = b;
				if (b > maxB) maxB = b;
			}

			foreach (var (spread, shift) in new[] { (maxR - minR, 16), (maxG - minG, 8), (maxB - minB, 0) }) {
				if (spread > bestSpread) {
					bestSpread = spread;
					bestIndex = index;
					bestShift = shift;
				}
			}
		}

		return (bestIndex, bestShift);
	}
}
