using SaneTui.Sane;

namespace SaneTui;

internal static class DumpCommand
{
	public static async Task<int> RunAsync(string[] deviceNames, CancellationToken cancellationToken)
	{
		await using var sane = await SaneLibrary.InitializeAsync();

		Console.WriteLine($"SANE {sane.Version}");

		var devices = await sane.GetDevicesAsync();

		Console.WriteLine($"{devices.Count} device(s) found:");

		foreach (var d in devices) {
			Console.WriteLine($"  {d.Name}  ({d.Vendor} {d.Model}, {d.Type})");
		}

		var names = deviceNames.Length > 0 ? deviceNames : [.. devices.Select(d => d.Name)];

		if (names.Length == 0) {
			Console.WriteLine("Nothing to dump. Pass a device name, e.g. 'test' or 'net:host:device'.");
			return 1;
		}

		var exitCode = 0;

		foreach (var name in names) {
			cancellationToken.ThrowIfCancellationRequested();

			Console.WriteLine();
			Console.WriteLine($"==== {name} ====");

			try {
				await using var device = await sane.OpenAsync(name);

				foreach (var o in device.Options) {
					Dump(o);
				}
			} catch (SaneException ex) {
				Console.WriteLine($"Failed to open: {ex.Message}");
				exitCode = 1;
			}
		}

		return exitCode;
	}

	private static void Dump(SaneOption o)
	{
		if (o.Type == SaneValueType.Group) {
			Console.WriteLine();
			Console.WriteLine($"-- {o.Title} --");
			return;
		}

		Console.WriteLine($"[{o.Index,3}] {(o.Name.Length > 0 ? o.Name : "(unnamed)")}: {o.Title}");
		Console.WriteLine($"      type={o.Type} unit={o.Unit} size={o.Size} caps={o.Capabilities}");

		if (o.Range is { } r) {
			Console.WriteLine($"      range: {r.Min:G}..{r.Max:G}{(r.Step == 0 ? "" : $" step {r.Step:G}")}");
		}

		if (o.NumberList is { } nums) {
			Console.WriteLine($"      one of: {string.Join(", ", nums.Select(n => n.ToString("G")))}");
		}

		if (o.StringList is { } strs) {
			Console.WriteLine($"      one of: {string.Join(" | ", strs)}");
		}

		if (o.ReadError is { } err) {
			Console.WriteLine($"      value: <read failed: {err}>");
		} else if (o.Value is not null) {
			Console.WriteLine($"      value: {Format(o.Value)}");
		}

		if (o.Description.Length > 0) {
			Console.WriteLine($"      {o.Description}");
		}
	}

	private static string Format(object value) => value switch {
		int[] a => $"[{string.Join(", ", a)}]",
		double[] a => $"[{string.Join(", ", a.Select(n => n.ToString("G")))}]",
		double n => n.ToString("G"),
		_ => value.ToString() ?? "",
	};
}
