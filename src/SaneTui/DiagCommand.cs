using SaneTui.Sane;

namespace SaneTui;

/// <summary>
/// Answers "why can't I see my scanner?": is libsane usable, what does SANE find, and are there USB devices that
/// look like scanners but that no driver claimed (usually a permissions problem).
/// </summary>
internal static class DiagCommand
{
	private const string UsbRoot = "/sys/bus/usb/devices";

	// 0 = fine, 1 = libsane works but no scanners were found, 2 = libsane couldn't be used
	public static async Task<int> RunAsync()
	{
		SaneLibrary sane;

		try {
			sane = await SaneLibrary.InitializeAsync();
		} catch (DllNotFoundException ex) {
			Console.WriteLine("libsane:  NOT LOADED");
			Console.WriteLine($"          {ex.Message}");
			Console.WriteLine("          Install it, for example 'apt install libsane1' (Debian, Ubuntu) or 'dnf install sane-backends-libs' (Fedora),");
			Console.WriteLine("          plus the driver package for your scanner.");

			return 2;
		} catch (SaneException ex) {
			Console.WriteLine("libsane:  loaded, but SANE failed to start");
			Console.WriteLine($"          {ex.Message}");

			return 2;
		}

		await using var _ = sane;

		Console.WriteLine($"libsane:  {LoadedLibraryPath() ?? "(loaded; path unknown)"}");
		Console.WriteLine($"version:  {sane.Version}");
		Console.WriteLine();

		// network drivers can stall for several seconds while they wait for answers
		Console.Error.WriteLine("Searching for scanners...");

		var all = await sane.GetDevicesAsync();
		var local = (await sane.GetDevicesAsync(localOnly: true)).Select(d => d.Name).ToHashSet();

		Console.WriteLine($"Scanners found by SANE: {all.Count}");

		foreach (var d in all) {
			Console.WriteLine($"  {d.Name}");
			Console.WriteLine($"      {d.Vendor} {d.Model} ({d.Type}){(local.Contains(d.Name) ? "" : "  [network]")}");
		}

		var unclaimed = FindUnclaimedUsbDevices(all, includeAnyVendorSpecific: all.Count == 0).ToList();

		if (unclaimed.Count > 0) {
			Console.WriteLine();
			Console.WriteLine("USB devices that look like scanners but that SANE didn't list (best effort; matched by bus and address):");

			foreach (var u in unclaimed) {
				Console.WriteLine($"  {u.Id}  {u.Description}  (bus {u.Bus:000}, device {u.Address:000})");
				Console.WriteLine($"      {u.Access}");
			}
		}

		if (all.Count == 0) {
			Console.WriteLine();
			Console.WriteLine("No scanners found. Check that the scanner is plugged in and turned on, that the driver for it is installed,");
			Console.WriteLine("and that your user may open the USB device (see any permission notes above).");

			return 1;
		}

		return 0;
	}

	/// <summary>Runs a command, turning a missing libsane into a short message instead of a stack trace.</summary>
	public static async Task<int> GuardAsync(Func<Task<int>> command)
	{
		try {
			return await command();
		} catch (DllNotFoundException ex) {
			Console.Error.WriteLine($"libsane could not be loaded: {ex.Message}");
			Console.Error.WriteLine("Run 'sane-tui diag' for details.");

			return 2;
		}
	}

	// the runtime doesn't say where it found the library, but the process's memory map does
	private static string? LoadedLibraryPath()
	{
		try {
			foreach (var line in File.ReadLines("/proc/self/maps")) {
				var slash = line.IndexOf('/');

				if (slash >= 0 && Path.GetFileName(line[slash..]).StartsWith("libsane.so", StringComparison.Ordinal)) {
					return line[slash..];
				}
			}
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}

		return null;
	}

	private sealed record UsbCandidate(int Bus, int Address, string Id, string Description, string Access);

	// Looks for USB devices with a still-image (06) or vendor-specific (ff) interface that no SANE device name points at.
	// Vendor-specific alone matches phones, dongles and more, so those are only shown when SANE found nothing at all.
	private static IEnumerable<UsbCandidate> FindUnclaimedUsbDevices(IReadOnlyList<SaneDeviceInfo> claimed, bool includeAnyVendorSpecific)
	{
		if (!Directory.Exists(UsbRoot)) {
			yield break;
		}

		foreach (var dir in Directory.EnumerateDirectories(UsbRoot)) {
			var name = Path.GetFileName(dir);

			// skip interfaces ("1-2:1.0") and root hubs ("usb1")
			if (name.Contains(':') || name.StartsWith("usb", StringComparison.Ordinal)) {
				continue;
			}

			if (!int.TryParse(ReadFile(dir, "busnum"), out var bus) || !int.TryParse(ReadFile(dir, "devnum"), out var address)) {
				continue;
			}

			var classes = Directory.EnumerateDirectories(dir, $"{name}:*").Select(i => ReadFile(i, "bInterfaceClass")).Append(ReadFile(dir, "bDeviceClass")).ToList();
			var stillImage = classes.Contains("06");

			if (!stillImage && !(includeAnyVendorSpecific && classes.Contains("ff"))) {
				continue;
			}

			if (claimed.Any(d => d.Name.Contains($"libusb:{bus:000}:{address:000}", StringComparison.Ordinal))) {
				continue;
			}

			var description = $"{ReadFile(dir, "manufacturer")} {ReadFile(dir, "product")}".Trim();

			yield return new UsbCandidate(bus, address, $"{ReadFile(dir, "idVendor")}:{ReadFile(dir, "idProduct")}", description.Length > 0 ? description : "(unnamed)", CheckAccess(bus, address));
		}
	}

	private static string CheckAccess(int bus, int address)
	{
		var node = $"/dev/bus/usb/{bus:000}/{address:000}";

		try {
			using var handle = File.OpenHandle(node, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

			return "You can open this device, so SANE has no driver for it or the driver isn't installed.";
		} catch (UnauthorizedAccessException) {
			return $"No permission to open {node}. This is the usual reason a plugged-in scanner isn't found: check udev rules and group membership (often 'scanner' or 'lp').";
		} catch (FileNotFoundException) {
			return $"{node} doesn't exist here (a container without USB access?).";
		} catch (DirectoryNotFoundException) {
			return $"{node} doesn't exist here (a container without USB access?).";
		} catch (IOException ex) {
			return $"Couldn't check access to {node}: {ex.Message}";
		}
	}

	private static string? ReadFile(string dir, string file)
	{
		try {
			return File.ReadAllText(Path.Combine(dir, file)).Trim();
		} catch (IOException) {
			return null;
		} catch (UnauthorizedAccessException) {
			return null;
		}
	}
}
