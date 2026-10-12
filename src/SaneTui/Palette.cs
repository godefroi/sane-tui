using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SaneTui;

/// <summary>
/// The colors. Terminal.Gui's built-in themes are either grayscale or only partly applied, so the screen defines its own
/// schemes: a dark blue-grey base with a blue accent, readable on any terminal that supports 24-bit color.
/// </summary>
internal static class Palette
{
	public const string Main = "SaneTui";
	public const string Accent = "SaneTuiAccent";
	public const string Canvas = "SaneTuiCanvas";
	public const string Muted = "SaneTuiMuted";

	private static readonly Color Background = new(26, 27, 38);
	private static readonly Color Text = new(192, 202, 245);
	private static readonly Color Field = new(41, 46, 66);
	private static readonly Color Blue = new(122, 162, 247);
	private static readonly Color Orange = new(255, 158, 100);
	private static readonly Color Green = new(158, 206, 106);
	private static readonly Color Purple = new(187, 154, 247);
	private static readonly Color Grey = new(86, 95, 137);
	private static readonly Color Black = new(13, 14, 20);
	private static readonly Color DarkOrange = new(140, 50, 20);

	/// <summary>Adds the schemes. Safe to call more than once.</summary>
	public static void Register()
	{
		if (SchemeManager.TryGetScheme(Main, out _)) {
			return;
		}

		SchemeManager.AddScheme(Main, Create(Background, Text));
		SchemeManager.AddScheme(Accent, Create(Background, Blue));
		SchemeManager.AddScheme(Canvas, Create(Black, Grey));
		SchemeManager.AddScheme(Muted, Create(Background, Grey));
	}

	private static Scheme Create(Color background, Color text) => new(new Attribute(text, background)) {
		Normal = new Attribute(text, background),
		HotNormal = new Attribute(Orange, background),
		Focus = new Attribute(Black, Blue),
		HotFocus = new Attribute(DarkOrange, Blue),
		Active = new Attribute(Black, Green),
		HotActive = new Attribute(DarkOrange, Green),
		Highlight = new Attribute(Black, Purple),
		Editable = new Attribute(text, Field),
		ReadOnly = new Attribute(text, Field),
		Disabled = new Attribute(Grey, background),
	};
}
