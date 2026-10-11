using System.Runtime.InteropServices;

namespace SaneTui.Sane;

/// <summary>
/// The entry point for talking to scanners through <see href="http://www.sane-project.org/">SANE</see> (Scanner Access Now Easy),
/// the scanner driver framework used on Linux. List scanners with <see cref="GetDevicesAsync"/>, then open one with <see cref="OpenAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Requires the native SANE library (<c>libsane.so.1</c>, packaged as <c>libsane1</c> or <c>sane-backends</c> on most distributions)
/// and the driver for your scanner. Linux only.
/// </para>
/// <para>
/// SANE keeps global state and its drivers aren't safe to call from several threads, so all calls are made on one dedicated
/// background thread and every instance of this class shares it. Instances are cheap handles on that one session: SANE is
/// shut down when the last instance is disposed, which also closes any device still open.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var sane = await SaneLibrary.InitializeAsync();
///
/// var scanners = await sane.GetDevicesAsync();
/// await using var device = await sane.OpenAsync(scanners[0].Name);
///
/// await device.SetAsync(device.GetOption("resolution"), 300);
///
/// SaneImage page = await device.ScanAsync();
/// </code>
/// </example>
public sealed class SaneLibrary : IAsyncDisposable
{
	private static readonly SemaphoreSlim Gate = new(1, 1); // serializes init/exit across instances
	private static SaneWorker? s_worker;
	private static Version? s_version;
	private static int s_instances;

	private readonly SaneWorker _worker;
	private int _disposed;

	private SaneLibrary(SaneWorker worker, Version version)
	{
		_worker = worker;
		Version = version;
	}

	/// <summary>The version of the SANE library that is installed.</summary>
	public Version Version { get; }

	internal SaneWorker Worker => _worker;

	/// <summary>Starts SANE, or joins the session if another instance already started it.</summary>
	/// <exception cref="DllNotFoundException">The native SANE library isn't installed.</exception>
	/// <exception cref="SaneException">SANE failed to start.</exception>
	public static async Task<SaneLibrary> InitializeAsync()
	{
		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			if (s_instances == 0) {
				var worker = new SaneWorker();

				try {
					var code = await worker.RunAsync(() => {
						Check(Native.sane_init(out var versionCode, nint.Zero));
						return versionCode;
					}).ConfigureAwait(false);

					// SANE_VERSION_MAJOR/MINOR/BUILD: 8 bits, 8 bits, 16 bits
					s_version = new Version((code >> 24) & 0xff, (code >> 16) & 0xff, code & 0xffff);
					s_worker = worker;
				} catch {
					worker.Dispose();
					throw;
				}
			}

			s_instances++;

			return new SaneLibrary(s_worker!, s_version!);
		} finally {
			Gate.Release();
		}
	}

	/// <summary>
	/// Lists the scanners SANE can currently see. Drivers probe for hardware (and, for network drivers, the network)
	/// each time this is called, so it can take a few seconds.
	/// </summary>
	/// <param name="localOnly">Leave out scanners reached over the network (through SANE's <c>net</c> driver and a remote <c>saned</c> server).</param>
	/// <returns>The scanners found, possibly none. Pass a <see cref="SaneDeviceInfo.Name"/> to <see cref="OpenAsync"/>.</returns>
	/// <exception cref="SaneException">SANE failed to list devices.</exception>
	public Task<IReadOnlyList<SaneDeviceInfo>> GetDevicesAsync(bool localOnly = false) => _worker.RunAsync<IReadOnlyList<SaneDeviceInfo>>(() => {
		Check(Native.sane_get_devices(out var list, localOnly ? 1 : 0));

		var devices = new List<SaneDeviceInfo>();

		if (list == nint.Zero) {
			return devices;
		}

		// the list is a NULL-terminated array of pointers, owned by the backend until the next sane_get_devices
		for (var i = 0; ; i++) {
			var p = Marshal.ReadIntPtr(list, i * nint.Size);

			if (p == nint.Zero) {
				break;
			}

			var d = Marshal.PtrToStructure<NativeDevice>(p);

			devices.Add(new SaneDeviceInfo(Str(d.Name), Str(d.Vendor), Str(d.Model), Str(d.Type)));
		}

		return devices;
	});

	/// <summary>Opens a scanner and reads its options. Dispose the result when done so other programs can use the scanner.</summary>
	/// <param name="deviceName">A <see cref="SaneDeviceInfo.Name"/> from <see cref="GetDevicesAsync"/>. A name SANE didn't list also works if the driver understands it, for example a network address.</param>
	/// <exception cref="SaneException">The device couldn't be opened: it doesn't exist, it's in use (<see cref="SaneStatus.DeviceBusy"/>), or access was denied (<see cref="SaneStatus.AccessDenied"/>).</exception>
	public async Task<SaneDevice> OpenAsync(string deviceName) => await _worker.RunAsync(() => {
		Check(Native.sane_open(deviceName, out var handle));

		try {
			return new SaneDevice(this, handle, SaneDevice.ReadOptions(handle));
		} catch {
			Native.sane_close(handle);
			throw;
		}
	}).ConfigureAwait(false);

	/// <summary>Releases this instance. The last instance to be disposed shuts SANE down, which closes any device still open.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) {
			return;
		}

		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			if (--s_instances == 0) {
				await _worker.RunAsync(Native.sane_exit).ConfigureAwait(false);

				_worker.Dispose();
				s_worker = null;
				s_version = null;
			}
		} finally {
			Gate.Release();
		}
	}

	internal static string Str(nint p) => Marshal.PtrToStringUTF8(p) ?? "";

	internal static void Check(SaneStatus status)
	{
		if (status != SaneStatus.Good) {
			throw new SaneException(status, $"{status}: {Str(Native.sane_strstatus(status))}");
		}
	}
}
