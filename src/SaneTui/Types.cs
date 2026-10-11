namespace SaneTui;

public enum SaneStatus
{
	Good = 0,
	Unsupported = 1,
	Cancelled = 2,
	DeviceBusy = 3,
	Invalid = 4,
	Eof = 5,
	Jammed = 6,
	NoDocs = 7,
	CoverOpen = 8,
	IoError = 9,
	NoMemory = 10,
	AccessDenied = 11,
}

public enum SaneValueType
{
	Bool = 0,
	Int = 1,
	Fixed = 2,
	String = 3,
	Button = 4,
	Group = 5,
}

public enum SaneUnit
{
	None = 0,
	Pixel = 1,
	Bit = 2,
	Millimeter = 3,
	Dpi = 4,
	Percent = 5,
	Microsecond = 6,
}

[Flags]
public enum SaneCapabilities
{
	None = 0,
	SoftSelect = 1,
	HardSelect = 2,
	SoftDetect = 4,
	Emulated = 8,
	Automatic = 16,
	Inactive = 32,
	Advanced = 64,
}

public sealed class SaneException(SaneStatus status, string message) : Exception(message)
{
	public SaneStatus Status { get; } = status;
}

public sealed record SaneDeviceInfo(string Name, string Vendor, string Model, string Type);

/// <summary>
/// Numeric limits of an option. Integer options use the same type; their bounds are whole numbers.
/// A <see cref="Step"/> of 0 means any value in the range is allowed.
/// </summary>
public sealed record SaneRange(double Min, double Max, double Step);

/// <summary>
/// One backend option. <see cref="Value"/> is null when the option can't be read (inactive, not detectable, button, group), or when the backend
/// refused the read, in which case <see cref="ReadError"/> says why.
/// Otherwise it is a <see cref="bool"/>, <see cref="int"/>, <see cref="double"/> (fixed-point), <see cref="string"/>,
/// or an <see cref="int"/>[] / <see cref="double"/>[] when the option holds more than one number (e.g. gamma tables).
/// At most one of <see cref="Range"/>, <see cref="NumberList"/> and <see cref="StringList"/> is set.
/// </summary>
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
	public bool IsActive => (Capabilities & SaneCapabilities.Inactive) == 0;

	public bool IsSettable => IsActive && (Capabilities & SaneCapabilities.SoftSelect) != 0;
}
