namespace SaneTui.Sane;

/// <summary>The result code of a SANE operation, as reported by the scanner driver (the "backend").</summary>
public enum SaneStatus
{
	/// <summary>The operation succeeded.</summary>
	Good = 0,

	/// <summary>The device or driver doesn't support the requested operation.</summary>
	Unsupported = 1,

	/// <summary>The operation was cancelled.</summary>
	Cancelled = 2,

	/// <summary>The device is in use, for example by another program. Try again later.</summary>
	DeviceBusy = 3,

	/// <summary>An argument or the data involved was invalid.</summary>
	Invalid = 4,

	/// <summary>There is no more data to read. Scanning treats this as the normal end of a page, so it isn't normally surfaced.</summary>
	Eof = 5,

	/// <summary>The document feeder is jammed.</summary>
	Jammed = 6,

	/// <summary>The document feeder is out of documents.</summary>
	NoDocs = 7,

	/// <summary>The scanner's cover is open.</summary>
	CoverOpen = 8,

	/// <summary>Communicating with the scanner failed, for example because it was unplugged or timed out.</summary>
	IoError = 9,

	/// <summary>The driver ran out of memory.</summary>
	NoMemory = 10,

	/// <summary>Access to the device was denied, for example by file permissions or a required password.</summary>
	AccessDenied = 11,
}

/// <summary>The kind of value a <see cref="SaneOption"/> holds.</summary>
public enum SaneValueType
{
	/// <summary>On or off. The value is a <see cref="bool"/>.</summary>
	Bool = 0,

	/// <summary>A whole number, or an array of them. The value is an <see cref="int"/> or <see cref="int"/>[].</summary>
	Int = 1,

	/// <summary>A number that can have a fractional part, or an array of them. The value is a <see cref="double"/> or <see cref="double"/>[].</summary>
	Fixed = 2,

	/// <summary>Text. The value is a <see cref="string"/>.</summary>
	String = 3,

	/// <summary>An action rather than a setting (eject a page, run calibration). It has no value; use <see cref="SaneDevice.PressAsync"/>.</summary>
	Button = 4,

	/// <summary>A heading that organizes the options that follow it in <see cref="SaneDevice.Options"/>. It has no value and can't be set.</summary>
	Group = 5,
}

/// <summary>The physical unit of a numeric option.</summary>
public enum SaneUnit
{
	/// <summary>The value has no unit.</summary>
	None = 0,

	/// <summary>Pixels.</summary>
	Pixel = 1,

	/// <summary>Bits.</summary>
	Bit = 2,

	/// <summary>Millimeters.</summary>
	Millimeter = 3,

	/// <summary>Dots per inch.</summary>
	Dpi = 4,

	/// <summary>Percent.</summary>
	Percent = 5,

	/// <summary>Microseconds.</summary>
	Microsecond = 6,
}

/// <summary>What can be done with an option, and whether it currently applies.</summary>
[Flags]
public enum SaneCapabilities
{
	/// <summary>No capabilities.</summary>
	None = 0,

	/// <summary>Software can set the value.</summary>
	SoftSelect = 1,

	/// <summary>Only the user can set the value, with a physical control on the scanner. Software can't.</summary>
	HardSelect = 2,

	/// <summary>Software can read the value.</summary>
	SoftDetect = 4,

	/// <summary>The driver emulates this option in software; the scanner hardware doesn't provide it.</summary>
	Emulated = 8,

	/// <summary>The driver can choose the value itself. See <see cref="SaneDevice.SetAutoAsync"/>.</summary>
	Automatic = 16,

	/// <summary>The option doesn't apply right now, usually because of another option's value. It can become active when other options change.</summary>
	Inactive = 32,

	/// <summary>A rarely needed option. Interfaces typically hide these by default.</summary>
	Advanced = 64,
}

/// <summary>What the driver reports happened as a result of setting an option.</summary>
[Flags]
public enum SaneSetInfo
{
	/// <summary>The value was applied as given and nothing else changed.</summary>
	None = 0,

	/// <summary>The driver adjusted the value (for example, rounded to what the hardware supports). Read the option from <see cref="SaneDevice.Options"/> to see what it kept.</summary>
	Inexact = 1,

	/// <summary>Other options changed: their values, their allowed ranges, or whether they're active. <see cref="SaneDevice.Options"/> is already refreshed.</summary>
	ReloadOptions = 2,

	/// <summary>The geometry of the resulting image (size, depth) changed.</summary>
	ReloadParameters = 4,
}

/// <summary>How the samples in a <see cref="SaneImage"/> are arranged.</summary>
public enum SanePixelFormat
{
	/// <summary>One sample per pixel: grayscale, or black and white at 1 bit per sample.</summary>
	Gray,

	/// <summary>Three samples per pixel, interleaved as red, green, blue.</summary>
	Rgb,
}

/// <summary>
/// A scanned page held in memory as raw pixels. Rows are <see cref="Stride"/> bytes apart, which can be more than
/// the pixels need. Scanners that send red, green and blue separately are already merged, so the data is always
/// gray or interleaved RGB.
/// </summary>
/// <remarks>
/// <para>
/// At 1 bit per sample, a set bit is black (the opposite of 8-bit gray, where 0 is black), and each row is padded to a whole byte.
/// 16-bit samples are in the machine's native byte order. Both are easy to get wrong when encoding to an image format.
/// </para>
/// <para>This is uncompressed pixel data. Encoding it to JPEG, TIFF, PDF or similar is up to the caller.</para>
/// </remarks>
/// <param name="Width">Pixels per row.</param>
/// <param name="Height">Number of rows. For scanners that don't know the page height in advance, this is what was actually received.</param>
/// <param name="Format">Gray or RGB.</param>
/// <param name="Depth">Bits per sample: 1, 8 or 16.</param>
/// <param name="Stride">Bytes from the start of one row to the start of the next. At least the width of the row in bytes.</param>
/// <param name="Data">The pixels: <c>Stride * Height</c> bytes.</param>
public sealed record SaneImage(int Width, int Height, SanePixelFormat Format, int Depth, int Stride, ReadOnlyMemory<byte> Data);

/// <summary>The part of the scanner's bed (or the paper) that gets scanned, in millimeters from the top-left corner.</summary>
/// <param name="X">Distance from the left edge to the left side of the area.</param>
/// <param name="Y">Distance from the top edge to the top of the area.</param>
/// <param name="Width">Width of the area.</param>
/// <param name="Height">Height of the area.</param>
public sealed record SaneScanArea(double X, double Y, double Width, double Height);

/// <summary>A SANE operation failed. <see cref="Status"/> says how.</summary>
/// <param name="status">The driver's result code.</param>
/// <param name="message">A description that includes the status and SANE's own explanation of it.</param>
public sealed class SaneException(SaneStatus status, string message) : Exception(message)
{
	/// <summary>The driver's result code, for handling specific cases such as <see cref="SaneStatus.Jammed"/> or <see cref="SaneStatus.NoDocs"/>.</summary>
	public SaneStatus Status { get; } = status;
}

/// <summary>A scanner that SANE knows about, as listed by <see cref="SaneLibrary.GetDevicesAsync"/>.</summary>
/// <param name="Name">The identifier to pass to <see cref="SaneLibrary.OpenAsync"/>. Its format depends on the driver, for example <c>driver:connection:address</c>. Treat it as opaque.</param>
/// <param name="Vendor">The manufacturer, for display.</param>
/// <param name="Model">The model name, for display.</param>
/// <param name="Type">What kind of device this is, for display, for example "flatbed scanner".</param>
public sealed record SaneDeviceInfo(string Name, string Vendor, string Model, string Type);

/// <summary>
/// The limits of a numeric option. Integer options use the same type; their bounds are whole numbers.
/// </summary>
/// <param name="Min">The smallest allowed value.</param>
/// <param name="Max">The largest allowed value.</param>
/// <param name="Step">The allowed increment above <paramref name="Min"/>. 0 means any value in the range is allowed.</param>
public sealed record SaneRange(double Min, double Max, double Step);

/// <summary>
/// One setting of a scanner: resolution, color mode, scan area, and so on. Which options exist, what they're called,
/// and what values they accept varies from driver to driver, so check the allowed values
/// (<see cref="Range"/>, <see cref="NumberList"/>, <see cref="StringList"/>) rather than assuming.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is null when the option can't be read (it's inactive, software can't detect it, or it's a button or
/// group), or when the driver refused the read, in which case <see cref="ReadError"/> says why. Otherwise it is a
/// <see cref="bool"/>, <see cref="int"/>, <see cref="double"/>, <see cref="string"/>, or an <see cref="int"/>[] or
/// <see cref="double"/>[] when the option holds several numbers (a gamma table, for example).
/// </para>
/// <para>
/// At most one of <see cref="Range"/>, <see cref="NumberList"/> and <see cref="StringList"/> is set; it says which values are allowed.
/// If none is set, any value of the right type is allowed.
/// </para>
/// <para>This is a snapshot. Setting any option replaces <see cref="SaneDevice.Options"/>, so look the option up again afterwards.</para>
/// </remarks>
/// <param name="Index">The option's position in the driver's list. Identifies the option when setting it.</param>
/// <param name="Name">A short identifier such as <c>resolution</c> or <c>tl-x</c>. SANE reserves some standard names, but drivers also add their own. Empty for groups, and not always a valid identifier (it can contain spaces).</param>
/// <param name="Title">A short label for display.</param>
/// <param name="Description">A longer explanation for display. Can be empty.</param>
/// <param name="Type">The kind of value.</param>
/// <param name="Unit">The physical unit, if the value is a number.</param>
/// <param name="Size">The size in bytes of the driver's buffer for the value. For numbers that's 4 per value, so values over 4 are arrays.</param>
/// <param name="Capabilities">What can be done with the option right now.</param>
/// <param name="Value">The current value, or null. See the remarks.</param>
/// <param name="Range">The allowed range, if the option is constrained to one.</param>
/// <param name="NumberList">The allowed numbers, if the option is constrained to a list.</param>
/// <param name="StringList">The allowed strings, exactly as the driver spells them, if the option is constrained to a list. Pass these back unchanged when setting.</param>
/// <param name="ReadError">Why the driver refused to report the value, if it did.</param>
public sealed record SaneOption(
	int Index,
	string Name,
	string Title,
	string Description,
	SaneValueType Type,
	SaneUnit Unit,
	int Size,
	SaneCapabilities Capabilities,
	object? Value,
	SaneRange? Range,
	IReadOnlyList<double>? NumberList,
	IReadOnlyList<string>? StringList,
	string? ReadError = null)
{
	/// <summary>False when the option doesn't apply right now. It can become active when other options change.</summary>
	public bool IsActive => (Capabilities & SaneCapabilities.Inactive) == 0;

	/// <summary>True when the option applies and software is allowed to set it.</summary>
	public bool IsSettable => IsActive && (Capabilities & SaneCapabilities.SoftSelect) != 0;
}
