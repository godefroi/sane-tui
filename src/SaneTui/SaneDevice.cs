using System.Runtime.InteropServices;
using System.Text;
using static SaneTui.SaneLibrary;

namespace SaneTui;

public sealed class SaneDevice : IAsyncDisposable
{
	private const double FixedScale = 1 << 16; // SANE_FIXED_SCALE_SHIFT is 16

	private readonly SaneLibrary _library;
	private nint _handle;

	internal SaneDevice(SaneLibrary library, nint handle, IReadOnlyList<SaneOption> options)
	{
		_library = library;
		_handle = handle;
		Options = options;
	}

	/// <summary>Snapshot taken when the device was opened. Option 0 (the option count) is not included.</summary>
	public IReadOnlyList<SaneOption> Options { get; }

	public async ValueTask DisposeAsync()
	{
		// after the library is disposed, sane_exit has already closed the handle
		if (_handle == nint.Zero || _library.Worker.IsDisposed) {
			return;
		}

		var handle = _handle;

		_handle = nint.Zero;

		await _library.Worker.RunAsync(() => Native.sane_close(handle)).ConfigureAwait(false);
	}

	internal static unsafe List<SaneOption> ReadOptions(nint handle)
	{
		var count = (int)(ReadValue(handle, 0, GetDescriptor(handle, 0)) ?? throw new InvalidOperationException("Option 0 has no value."));
		var options = new List<SaneOption>(count);

		for (var i = 1; i < count; i++) {
			var d = GetDescriptor(handle, i);
			var isNumber = d.Type is SaneValueType.Bool or SaneValueType.Int or SaneValueType.Fixed;
			var readable = (d.Capabilities & (SaneCapabilities.Inactive | SaneCapabilities.SoftDetect)) == SaneCapabilities.SoftDetect;
			var isFixed = d.Type == SaneValueType.Fixed;

			SaneRange? range = null;
			double[]? numbers = null;
			string[]? strings = null;

			switch (d.ConstraintType) {
				case SaneConstraintKind.Range: {
					var r = *(NativeRange*)d.Constraint;

					range = new SaneRange(ToNumber(r.Min, isFixed), ToNumber(r.Max, isFixed), ToNumber(r.Quant, isFixed));

					break;
				}
				case SaneConstraintKind.WordList: {
					// first word is the count, the values follow
					var words = (int*)d.Constraint;

					numbers = new double[words[0]];

					for (var n = 0; n < numbers.Length; n++) {
						numbers[n] = ToNumber(words[n + 1], isFixed);
					}

					break;
				}
				case SaneConstraintKind.StringList: {
					var list = new List<string>();

					for (var s = (nint*)d.Constraint; *s != nint.Zero; s++) {
						list.Add(Str(*s));
					}

					strings = [.. list];

					break;
				}
			}

			object? value = null;
			string? readError = null;

			if (readable && (isNumber || d.Type == SaneValueType.String)) {
				// a backend refusing one option shouldn't make the whole device unusable
				try {
					value = ReadValue(handle, i, d);
				} catch (SaneException ex) {
					readError = ex.Message;
				}
			}

			options.Add(new SaneOption(i, Str(d.Name), Str(d.Title), Str(d.Description), d.Type, d.Unit, d.Size, d.Capabilities, value, range, numbers, strings, readError));
		}

		return options;
	}

	private static unsafe NativeOptionDescriptor GetDescriptor(nint handle, int index)
	{
		var p = Native.sane_get_option_descriptor(handle, index);

		if (p == nint.Zero) {
			throw new InvalidOperationException($"No descriptor for option {index}.");
		}

		return *(NativeOptionDescriptor*)p;
	}

	private static unsafe object? ReadValue(nint handle, int index, NativeOptionDescriptor d)
	{
		// the backend writes d.Size bytes: 4 per number (arrays are possible) or the whole string buffer
		var buffer = new byte[d.Size];

		fixed (byte* p = buffer) {
			Check(Native.sane_control_option(handle, index, SaneAction.GetValue, p, out _));
		}

		switch (d.Type) {
			case SaneValueType.String:
				var length = Array.IndexOf(buffer, (byte)0);

				return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
			case SaneValueType.Bool:
				return MemoryMarshal.Read<int>(buffer) != 0;
		}

		var words = MemoryMarshal.Cast<byte, int>(buffer);

		return (d.Type, words.Length) switch {
			(SaneValueType.Int, 1) => words[0],
			(SaneValueType.Int, _) => words.ToArray(),
			(SaneValueType.Fixed, 1) => ToNumber(words[0], true),
			_ => words.ToArray().Select(w => ToNumber(w, true)).ToArray(),
		};
	}

	private static double ToNumber(int word, bool isFixed) => isFixed ? word / FixedScale : word;
}
