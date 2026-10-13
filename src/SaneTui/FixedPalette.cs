using Terminal.Gui.Drawing;

namespace SaneTui;

/// <summary>
/// A palette chosen once and used for every encode, ignoring the colors it's asked about. Picking a palette is most of the cost of
/// encoding a Sixel image, and the picture only changes by being dimmed, so one palette serves every redraw.
/// </summary>
internal sealed class FixedPalette(IReadOnlyList<Color> palette) : IPaletteBuilder
{
	public List<Color> BuildPalette(List<Color> colors, int maxColors) => [.. palette.Take(maxColors)];
}
