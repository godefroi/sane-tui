using System.Runtime.InteropServices;

namespace SaneTui.Sane;

// ponytail: Linux only. libsane.so.1 is the SONAME there; add a NativeLibrary resolver if macOS matters.
// The authorize callback is not wired up (passed as NULL), so backends that need credentials will fail to open.
internal static unsafe partial class Native
{
	private const string Lib = "libsane.so.1";

	[LibraryImport(Lib)]
	public static partial SaneStatus sane_init(out int versionCode, nint authorize);

	[LibraryImport(Lib)]
	public static partial void sane_exit();

	[LibraryImport(Lib)]
	public static partial SaneStatus sane_get_devices(out nint deviceList, int localOnly);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	public static partial SaneStatus sane_open(string name, out nint handle);

	[LibraryImport(Lib)]
	public static partial void sane_close(nint handle);

	[LibraryImport(Lib)]
	public static partial nint sane_get_option_descriptor(nint handle, int n);

	[LibraryImport(Lib)]
	public static partial SaneStatus sane_control_option(nint handle, int n, SaneAction action, void* value, out int info);

	[LibraryImport(Lib)]
	public static partial SaneStatus sane_start(nint handle);

	[LibraryImport(Lib)]
	public static partial SaneStatus sane_get_parameters(nint handle, out NativeParameters parameters);

	[LibraryImport(Lib)]
	public static partial SaneStatus sane_read(nint handle, byte* buffer, int maxLength, out int length);

	// the one call that is allowed from another thread while the worker is blocked in sane_read
	[LibraryImport(Lib)]
	public static partial void sane_cancel(nint handle);

	[LibraryImport(Lib)]
	public static partial nint sane_strstatus(SaneStatus status);
}

internal enum SaneFrameFormat
{
	Gray = 0,
	Rgb = 1,
	Red = 2,
	Green = 3,
	Blue = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeParameters
{
	public SaneFrameFormat Format;
	public int LastFrame;
	public int BytesPerLine;
	public int PixelsPerLine;
	public int Lines; // -1 when unknown (hand scanners)
	public int Depth;
}

internal enum SaneAction
{
	GetValue = 0,
	SetValue = 1,
	SetAuto = 2,
}

internal enum SaneConstraintKind
{
	None = 0,
	Range = 1,
	WordList = 2,
	StringList = 3,
}

// All the string fields are const char* owned by the backend; never freed from our side.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDevice
{
	public nint Name;
	public nint Vendor;
	public nint Model;
	public nint Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOptionDescriptor
{
	public nint Name;
	public nint Title;
	public nint Description;
	public SaneValueType Type;
	public SaneUnit Unit;
	public int Size;
	public SaneCapabilities Capabilities;
	public SaneConstraintKind ConstraintType;
	public nint Constraint; // union of range*, word_list*, string_list*; which one is selected by ConstraintType
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRange
{
	public int Min;
	public int Max;
	public int Quant;
}
