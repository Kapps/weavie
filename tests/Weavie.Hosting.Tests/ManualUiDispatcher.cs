namespace Weavie.Hosting.Tests;

internal sealed class ManualUiDispatcher : IUiDispatcher {
	private readonly Queue<Action> _pending = [];
	private readonly object _gate = new();
	private TaskCompletionSource _posted = NewCompletion();
	private bool _paused;

	public ManualUiDispatcher(bool paused) {
		_paused = paused;
	}

	public void Post(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		lock (_gate) {
			if (_paused) {
				_pending.Enqueue(action);
				_posted.TrySetResult();
				return;
			}
		}

		action();
	}

	public void Pause() {
		lock (_gate) {
			_paused = true;
			if (_pending.Count == 0) {
				_posted = NewCompletion();
			}
		}
	}

	public Task WaitForPostAsync() {
		lock (_gate) {
			return _pending.Count > 0 ? Task.CompletedTask : _posted.Task;
		}
	}

	public void RunPending() {
		while (true) {
			Action action;
			lock (_gate) {
				if (_pending.Count == 0) {
					return;
				}

				action = _pending.Dequeue();
			}

			action();
		}
	}

	private static TaskCompletionSource NewCompletion() =>
		new(TaskCreationOptions.RunContinuationsAsynchronously);
}
