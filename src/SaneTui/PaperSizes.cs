namespace SaneTui;

/// <summary>Standard paper sizes in millimeters, portrait.</summary>
internal static class PaperSizes
{
	private static readonly Dictionary<string, (double Width, double Height)> Sizes = new(StringComparer.OrdinalIgnoreCase) {
		["a4"] = (210, 297),
		["a5"] = (148, 210),
		["a6"] = (105, 148),
		["letter"] = (215.9, 279.4), // 8.5 x 11 in
		["legal"] = (215.9, 355.6), // 8.5 x 14 in
	};

	public static IEnumerable<string> Names => Sizes.Keys;

	public static bool TryGet(string name, out (double Width, double Height) size) => Sizes.TryGetValue(name, out size);
}
