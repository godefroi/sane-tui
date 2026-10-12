using System.Globalization;
using SaneTui.Sane;

namespace SaneTui;

/// <summary>A problem with what the user asked for. The message is shown as is, so it says what to do about it.</summary>
internal sealed class CommandException(string message) : Exception(message);

/// <summary>Turns text from the command line into option values, checking them against what the device allows.</summary>
internal static class OptionValues
{
	private static readonly string[] True = ["true", "yes", "on", "1"];
	private static readonly string[] False = ["false", "no", "off", "0"];

	public static SaneOption Find(SaneDevice device, string name) =>
		device.Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
		?? throw new CommandException($"The device has no option '{name}'. 'sane-tui dump' lists the options it has.");

	/// <summary>Converts <paramref name="text"/> to a value <see cref="SaneDevice.SetAsync"/> accepts for this option.</summary>
	public static object Parse(SaneOption option, string text)
	{
		switch (option.Type) {
			case SaneValueType.Bool:
				if (True.Contains(text, StringComparer.OrdinalIgnoreCase)) {
					return true;
				}

				if (False.Contains(text, StringComparer.OrdinalIgnoreCase)) {
					return false;
				}

				throw new CommandException($"'{text}' isn't a yes/no value for '{option.Name}'. Use true or false.");

			case SaneValueType.String:
				// the driver wants its own spelling ("Color", not "color"), so match loosely and send its version
				if (option.StringList is { } allowed) {
					return allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
						?? throw new CommandException($"'{text}' isn't a valid value for '{option.Name}'. Choose one of: {string.Join(", ", allowed)}.");
				}

				return text;

			case SaneValueType.Int or SaneValueType.Fixed:
				return ParseNumber(option, text);

			default:
				throw new CommandException($"'{option.Name}' is a {option.Type.ToString().ToLowerInvariant()}, not a setting that has a value.");
		}
	}

	private static double ParseNumber(SaneOption option, string text)
	{
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
			throw new CommandException($"'{text}' isn't a number (for '{option.Name}').");
		}

		if (option.Type == SaneValueType.Int && number != Math.Floor(number)) {
			throw new CommandException($"'{option.Name}' takes a whole number, not '{text}'.");
		}

		var unit = option.Unit == SaneUnit.None ? "" : $" {Unit(option.Unit)}";

		if (option.Range is { } range && (number < range.Min || number > range.Max)) {
			throw new CommandException($"{text} is outside what '{option.Name}' allows: {range.Min:G} to {range.Max:G}{unit}.");
		}

		if (option.NumberList is { } list && !list.Any(v => Math.Abs(v - number) < 1e-6)) {
			throw new CommandException($"{text} isn't a value '{option.Name}' allows. Choose one of: {string.Join(", ", list.Select(v => v.ToString("G", CultureInfo.InvariantCulture)))}{unit}.");
		}

		return number;
	}

	private static string Unit(SaneUnit unit) => unit switch {
		SaneUnit.Dpi => "dpi",
		SaneUnit.Millimeter => "mm",
		SaneUnit.Pixel => "pixels",
		SaneUnit.Bit => "bits",
		SaneUnit.Percent => "%",
		SaneUnit.Microsecond => "µs",
		_ => "",
	};
}
