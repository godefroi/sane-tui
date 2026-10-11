namespace SaneTui.Sane;

public sealed partial class SaneDevice
{
	/// <summary>
	/// Chooses the part of the scanner to scan, in millimeters from the top-left corner. Without arguments, selects the largest area the device supports.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Scanners differ in what area they scan by default (some start with a small one), so set it explicitly. This works through the
	/// standard <c>tl-x</c>, <c>tl-y</c>, <c>br-x</c> and <c>br-y</c> options, so it needs a device that has them in millimeters.
	/// </para>
	/// <para>
	/// An area that extends past what the device supports is cut down to fit rather than rejected, so a standard paper size
	/// can be requested from any scanner. Check the returned area for what was actually selected.
	/// </para>
	/// <para>Set the source (flatbed or feeder) first: the supported area can depend on it.</para>
	/// </remarks>
	/// <param name="width">The width to scan, or null for as far right as the device allows.</param>
	/// <param name="height">The height to scan, or null for as far down as the device allows.</param>
	/// <param name="x">The distance of the area's left edge from the left edge of the scanner.</param>
	/// <param name="y">The distance of the area's top edge from the top edge of the scanner.</param>
	/// <returns>The area now selected, as the driver reports it. It can differ slightly from the request because of rounding.</returns>
	/// <exception cref="ArgumentOutOfRangeException">A size isn't positive, a position is negative, or the position is outside the device's area.</exception>
	/// <exception cref="NotSupportedException">The device doesn't have the four standard geometry options, or doesn't measure them in millimeters.</exception>
	/// <exception cref="InvalidOperationException">An option is inactive, or a scan is in progress.</exception>
	public async Task<SaneScanArea> SetScanAreaAsync(double? width = null, double? height = null, double x = 0, double y = 0)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(x);
		ArgumentOutOfRangeException.ThrowIfNegative(y);

		if (width is <= 0) {
			throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
		}

		if (height is <= 0) {
			throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be positive.");
		}

		var (maxRight, minLeft) = Limits("br-x", "tl-x");
		var (maxBottom, minTop) = Limits("br-y", "tl-y");

		var left = Math.Max(x, minLeft);
		var top = Math.Max(y, minTop);

		if (left >= maxRight || top >= maxBottom) {
			throw new ArgumentOutOfRangeException(left >= maxRight ? nameof(x) : nameof(y), "The position is outside the area the device can scan.");
		}

		var right = Math.Min(left + (width ?? double.PositiveInfinity), maxRight);
		var bottom = Math.Min(top + (height ?? double.PositiveInfinity), maxBottom);

		// widen the bottom-right corner first so the top-left one can move anywhere; backends may reject a top-left past the current bottom-right
		await SetMillimetersAsync("br-x", maxRight).ConfigureAwait(false);
		await SetMillimetersAsync("br-y", maxBottom).ConfigureAwait(false);
		await SetMillimetersAsync("tl-x", left).ConfigureAwait(false);
		await SetMillimetersAsync("tl-y", top).ConfigureAwait(false);
		await SetMillimetersAsync("br-x", right).ConfigureAwait(false);
		await SetMillimetersAsync("br-y", bottom).ConfigureAwait(false);

		var actualLeft = ReadMillimeters("tl-x");
		var actualTop = ReadMillimeters("tl-y");

		return new SaneScanArea(actualLeft, actualTop, ReadMillimeters("br-x") - actualLeft, ReadMillimeters("br-y") - actualTop);
	}

	// the option may be an int or a fixed-point number depending on the driver
	private double ReadMillimeters(string name) => GetOption(name).Value is { } value ? Convert.ToDouble(value) : throw new NotSupportedException($"The value of '{name}' can't be read.");

	// the far edge's maximum, and the near edge's minimum
	private (double Max, double Min) Limits(string farName, string nearName)
	{
		var far = GeometryOption(farName);
		var near = GeometryOption(nearName);

		return (far.Range!.Max, near.Range!.Min);
	}

	private SaneOption GeometryOption(string name)
	{
		var option = _options.FirstOrDefault(o => o.Name == name) ?? throw new NotSupportedException($"The device has no '{name}' option.");

		if (option.Unit != SaneUnit.Millimeter || option.Range is null || option.Type is not (SaneValueType.Fixed or SaneValueType.Int)) {
			throw new NotSupportedException($"The '{name}' option isn't a millimeter range, which is the only kind supported.");
		}

		return option;
	}

	private Task<SaneSetInfo> SetMillimetersAsync(string name, double value)
	{
		var option = GeometryOption(name);

		// an Int option rejects fractions; round to the nearest millimeter
		return SetAsync(option, option.Type == SaneValueType.Int ? Math.Round(value) : value);
	}
}
