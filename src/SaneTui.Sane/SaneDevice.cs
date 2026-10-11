using System.Runtime.InteropServices;
using System.Text;
using static SaneTui.Sane.SaneLibrary;

namespace SaneTui.Sane;

/// <summary>
/// An open scanner. Get one from <see cref="SaneLibrary.OpenAsync"/>. Read and change its settings through
/// <see cref="Options"/> and <see cref="SetAsync"/>, and scan with <see cref="ScanAsync"/> or <see cref="ScanBatchAsync"/>.
/// </summary>
/// <remarks>
/// Only one operation can use the scanner at a time. Options can't be changed while a scan is running, and a second
/// scan can't start until the first finishes. Dispose the device when done; scanners generally can't be shared with other programs while it's open.
/// </remarks>
public sealed partial class SaneDevice : IAsyncDisposable
{
	private const double FixedScale = 1 << 16; // SANE_FIXED_SCALE_SHIFT is 16

	private readonly SaneLibrary _library;
	private nint _handle;
	private volatile IReadOnlyList<SaneOption> _options;

	internal SaneDevice(SaneLibrary library, nint handle, IReadOnlyList<SaneOption> options)
	{
		_library = library;
		_handle = handle;
		_options = options;
	}

	/// <summary>
	/// Current state of every option. Option 0 (the option count) is not included. Replaced as a whole after
	/// each change, so records you're holding go stale; look the option up again after setting anything.
	/// </summary>
	public IReadOnlyList<SaneOption> Options => _options;

	/// <summary>Finds an option by its <see cref="SaneOption.Name"/> in the current <see cref="Options"/>.</summary>
	/// <exception cref="KeyNotFoundException">The device has no option with that name. Names vary between drivers, so check <see cref="Options"/> for what's there.</exception>
	public SaneOption GetOption(string name) => _options.FirstOrDefault(o => o.Name == name) ?? throw new KeyNotFoundException($"The device has no option named '{name}'.");

	/// <summary>
	/// Sets an option. A <see cref="SaneValueType.Bool"/> option takes a <see cref="bool"/>, a <see cref="SaneValueType.String"/> option
	/// a <see cref="string"/>, and <see cref="SaneValueType.Int"/> and <see cref="SaneValueType.Fixed"/> options a number
	/// (<see cref="int"/> or <see cref="double"/>; an Int option rejects fractions). Options holding several numbers take an array.
	/// </summary>
	/// <remarks>
	/// The driver may adjust the value (for example, round a resolution to one the hardware supports), and setting one option
	/// can change others. <see cref="Options"/> is refreshed afterwards, so read the outcome from there. Pass values from the option's
	/// allowed <see cref="SaneOption.Range"/>, <see cref="SaneOption.NumberList"/> or <see cref="SaneOption.StringList"/>.
	/// </remarks>
	/// <param name="option">The option to set. Only its <see cref="SaneOption.Index"/> is used, so a stale copy is fine.</param>
	/// <param name="value">The new value.</param>
	/// <returns>What the driver reports happened. See <see cref="SaneSetInfo"/>.</returns>
	/// <exception cref="InvalidOperationException">The option is inactive or not software-settable, or a scan is in progress.</exception>
	/// <exception cref="ArgumentException">The value has the wrong type, doesn't fit the option, or a string is longer than the driver's buffer.</exception>
	/// <exception cref="SaneException">The driver rejected the value, for example because it's outside the allowed range (<see cref="SaneStatus.Invalid"/>).</exception>
	public Task<SaneSetInfo> SetAsync(SaneOption option, object value) => ControlAsync(option, SaneAction.SetValue, value);

	/// <summary>Lets the driver pick the value. Only valid for options with the <see cref="SaneCapabilities.Automatic"/> capability.</summary>
	/// <param name="option">The option to set automatically.</param>
	/// <returns>What the driver reports happened. See <see cref="SaneSetInfo"/>.</returns>
	/// <exception cref="InvalidOperationException">The option is inactive or has no automatic mode, or a scan is in progress.</exception>
	public Task<SaneSetInfo> SetAutoAsync(SaneOption option) => ControlAsync(option, SaneAction.SetAuto, null);

	/// <summary>Presses a <see cref="SaneValueType.Button"/> option, which triggers an action such as ejecting a page or running calibration.</summary>
	/// <param name="button">The button option.</param>
	/// <returns>What the driver reports happened. See <see cref="SaneSetInfo"/>.</returns>
	/// <exception cref="ArgumentException">The option isn't a button.</exception>
	/// <exception cref="InvalidOperationException">The button is inactive or not software-settable, or a scan is in progress.</exception>
	public Task<SaneSetInfo> PressAsync(SaneOption button)
	{
		if (button.Type != SaneValueType.Button) {
			throw new ArgumentException($"Option '{button.Name}' is not a button.", nameof(button));
		}

		return ControlAsync(button, SaneAction.SetValue, null);
	}

	/// <summary>Closes the scanner so other programs can use it.</summary>
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

	private nint Handle() => _handle != nint.Zero ? _handle : throw new ObjectDisposedException(nameof(SaneDevice));

	private Task<SaneSetInfo> ControlAsync(SaneOption option, SaneAction action, object? value)
	{
		if (Volatile.Read(ref _scanning) != 0) {
			throw new InvalidOperationException("Options can't be changed while a scan is in progress.");
		}

		return _library.Worker.RunAsync(() => {
			var handle = Handle();

			// the caller's record may be stale: what the backend says right now is what counts
			var d = GetDescriptor(handle, option.Index);

			if ((d.Capabilities & SaneCapabilities.Inactive) != 0) {
				throw new InvalidOperationException($"Option '{option.Name}' is inactive.");
			}

			if (action == SaneAction.SetValue && (d.Capabilities & SaneCapabilities.SoftSelect) == 0) {
				throw new InvalidOperationException($"Option '{option.Name}' can't be set from software.");
			}

			if (action == SaneAction.SetAuto && (d.Capabilities & SaneCapabilities.Automatic) == 0) {
				throw new InvalidOperationException($"Option '{option.Name}' has no automatic mode.");
			}

			// an empty array pins to a null pointer, which is what SANE wants for buttons and "auto"
			var buffer = action == SaneAction.SetValue ? Encode(d, value) : [];
			SaneStatus status;
			int info;

			unsafe {
				fixed (byte* p = buffer) {
					status = Native.sane_control_option(handle, option.Index, action, p, out info);
				}
			}

			Check(status);

			// even without ReloadOptions an inexact set changes values, and re-reading ~50 options is cheap
			_options = ReadOptions(handle);

			return (SaneSetInfo)info;
		});
	}

	// the backend writes d.Size bytes for numbers (4 each) or the whole string buffer, so the buffer is always exactly that
	private static byte[] Encode(NativeOptionDescriptor d, object? value)
	{
		switch (d.Type) {
			case SaneValueType.Button:
				return [];
			case SaneValueType.Group:
				throw new InvalidOperationException("Groups have no value.");
			case SaneValueType.Bool: {
				if (value is not bool b) {
					throw new ArgumentException("A bool option needs a bool value.", nameof(value));
				}

				var buffer = new byte[d.Size];

				MemoryMarshal.Write(buffer, b ? 1 : 0); // backends compare against SANE_TRUE, so exactly 0 or 1

				return buffer;
			}
			case SaneValueType.String: {
				if (value is not string s) {
					throw new ArgumentException("A string option needs a string value.", nameof(value));
				}

				var bytes = Encoding.UTF8.GetBytes(s);

				if (bytes.Length >= d.Size) {
					throw new ArgumentException($"The value is {bytes.Length} bytes; this option holds at most {d.Size - 1}.", nameof(value));
				}

				var buffer = new byte[d.Size];

				bytes.CopyTo(buffer, 0);

				return buffer;
			}
		}

		double[] numbers = value switch {
			int i => [i],
			double x => [x],
			int[] a => a.Select(n => (double)n).ToArray(),
			double[] a => a,
			_ => throw new ArgumentException("A numeric option needs an int, a double, or an array of them.", nameof(value)),
		};

		if (numbers.Length != d.Size / 4) {
			throw new ArgumentException($"This option holds {d.Size / 4} value(s); got {numbers.Length}.", nameof(value));
		}

		var words = new int[numbers.Length];

		for (var i = 0; i < numbers.Length; i++) {
			var n = d.Type == SaneValueType.Fixed ? Math.Round(numbers[i] * FixedScale) : numbers[i];

			if (n != Math.Floor(n) || n < int.MinValue || n > int.MaxValue) {
				throw new ArgumentException($"{numbers[i]} doesn't fit this {d.Type} option (Int options take whole numbers).", nameof(value));
			}

			words[i] = (int)n;
		}

		return MemoryMarshal.AsBytes<int>(words).ToArray();
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
