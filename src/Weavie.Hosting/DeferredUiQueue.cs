namespace Weavie.Hosting;

/// <summary>
/// Runs actions on the UI thread after the caller's UI work has returned, in the order they were queued.
/// <see cref="IUiDispatcher.Post"/> runs inline when already on the UI thread (Windows, macOS), so work that must
/// not run inside the current message handler goes through here: a menu action can open a native dialog the user
/// leaves up past any message deadline, or tear down the window whose handler is still running. Close it with its
/// owner: actions that haven't started by then are dropped, since the dispatcher can outlive the owner (macOS).
/// </summary>
internal sealed class DeferredUiQueue(IUiDispatcher ui) {
	private readonly Lock _gate = new();
	private Task _tail = Task.CompletedTask;
	private volatile bool _closed;

	public void Enqueue(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		lock (_gate) {
			// Posting from a pool thread always queues behind the caller's UI work; the chain keeps the order.
			_tail = _tail.ContinueWith(_ => ui.Post(() => {
				if (!_closed) {
					action();
				}
			}), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
		}
	}

	/// <summary>Drops every action that hasn't started yet; call when the owner is torn down.</summary>
	public void Close() => _closed = true;
}
