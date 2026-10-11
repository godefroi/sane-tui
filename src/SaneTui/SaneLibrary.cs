using System.Runtime.InteropServices;

namespace SaneTui;

/// <summary>
/// A handle on the process-wide SANE session. sane_init/sane_exit and the backends' state are global to the process,
/// so every instance shares one worker thread; sane_exit runs when the last instance is disposed.
/// </summary>
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

	public Version Version { get; }

	internal SaneWorker Worker => _worker;

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

	/// <param name="localOnly">Skip devices reached through the net backend (saned).</param>
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

	public async Task<SaneDevice> OpenAsync(string deviceName) => await _worker.RunAsync(() => {
		Check(Native.sane_open(deviceName, out var handle));

		try {
			return new SaneDevice(this, handle, SaneDevice.ReadOptions(handle));
		} catch {
			Native.sane_close(handle);
			throw;
		}
	}).ConfigureAwait(false);

	/// <summary>The last instance to be disposed calls sane_exit, which closes any device still open.</summary>
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
