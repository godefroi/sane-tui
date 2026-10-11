using System.Collections.Concurrent;

namespace SaneTui;

/// <summary>
/// Runs every SANE call on one dedicated thread. Backends aren't documented as thread-safe and
/// sane_read blocks, so callers get Tasks while the native side only ever sees a single thread.
/// </summary>
internal sealed class SaneWorker : IDisposable
{
	private readonly BlockingCollection<Action> _queue = [];
	private readonly Thread _thread;

	public SaneWorker()
	{
		_thread = new Thread(Loop) { Name = "SaneTui worker", IsBackground = true };
		_thread.Start();
	}

	public bool IsDisposed => _queue.IsAddingCompleted;

	public Task<T> RunAsync<T>(Func<T> work)
	{
		var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

		try {
			_queue.Add(() => {
				try {
					tcs.SetResult(work());
				} catch (Exception ex) {
					tcs.SetException(ex);
				}
			});
		} catch (InvalidOperationException) {
			throw new ObjectDisposedException(nameof(SaneLibrary));
		}

		return tcs.Task;
	}

	public Task RunAsync(Action work) => RunAsync<object?>(() => { work(); return null; });

	// Work already queued still runs; the thread exits once the queue drains.
	public void Dispose() => _queue.CompleteAdding();

	private void Loop()
	{
		foreach (var work in _queue.GetConsumingEnumerable()) {
			work();
		}
	}
}
