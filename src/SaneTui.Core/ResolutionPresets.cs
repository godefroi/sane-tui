using SaneTui.Sane;

namespace SaneTui;

/// <summary>
/// The resolutions to offer for a scanner: the usual ones, minus any it doesn't support. Scanners disagree about what they
/// allow (a continuous range, a short list), so a fixed menu would offer values that fail.
/// </summary>
internal static class ResolutionPresets
{
	private const double Preferred = 300; // good for text, and what the scan command defaults to

	private static readonly double[] Common = [50, 75, 100, 150, 200, 240, 300, 360, 400, 600, 1200];

	/// <summary>The common resolutions the option allows. If none are allowed, the option's own values, so there's always something to pick.</summary>
	public static IReadOnlyList<double> For(SaneOption? resolution)
	{
		if (resolution is null) {
			return Common;
		}

		if (resolution.NumberList is { } list) {
			var shared = Common.Where(c => list.Any(v => Math.Abs(v - c) < 1e-6)).ToList();

			return shared.Count > 0 ? shared : [.. list.Order()];
		}

		if (resolution.Range is { } range) {
			var shared = Common.Where(c => c >= range.Min && c <= range.Max && OnStep(c, range)).ToList();

			return shared.Count > 0 ? shared : [range.Min, range.Max];
		}

		return Common;
	}

	/// <summary>The choice closest to 300 dpi.</summary>
	public static double Default(IReadOnlyList<double> choices) => choices.OrderBy(c => Math.Abs(c - Preferred)).First();

	// a step of 0 means any value is fine
	private static bool OnStep(double value, SaneRange range)
	{
		if (range.Step <= 0) {
			return true;
		}

		var steps = (value - range.Min) / range.Step;

		return Math.Abs(steps - Math.Round(steps)) < 1e-6;
	}
}
