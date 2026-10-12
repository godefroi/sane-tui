using SaneTui.Sane;

namespace SaneTui;

/// <summary>Which scanner options with a list of allowed strings (source, mode) are worth offering, and what to preselect.</summary>
internal static class StringChoices
{
	/// <summary>
	/// The values to offer and the one to preselect, or null if the option shouldn't be offered. An inactive option has no
	/// value to preselect and setting it fails, so it's left out: a flatbed scanner lists its transparency sources, for
	/// example, but marks the option inactive until a transparency adapter is attached.
	/// </summary>
	public static (IReadOnlyList<string> Choices, string Selected)? For(SaneOption? option)
	{
		if (option is not { IsSettable: true, StringList: { Count: > 0 } choices }) {
			return null;
		}

		// the current value, if the scanner reports one it also lists; otherwise the first choice
		var selected = option.Value is string current && choices.Contains(current) ? current : choices[0];

		return (choices, selected);
	}
}
