using Weavie.Core.Commands;

namespace Weavie.Hosting.Messaging;

/// <summary>
/// Serial admission for one feature. A turn ends when its real handler returns, not when its deadline does, so a
/// timed-out handler can never overlap its successor; only a droppable, read-only one gives up its turn early.
/// </summary>
internal sealed class FeatureLane : IDisposable {
	private readonly object _gate = new();
	private readonly Func<MessageOperation?> _runningOperation;
	private Task _tail = Task.CompletedTask;
	private MessageOperation? _running;
	private bool _disposed;

	public FeatureLane() {
		_runningOperation = () => {
			lock (_gate) {
				return _running;
			}
		};
	}

	public Task<T> Enqueue<T>(MessageOperation operation, Func<Task<T>> work, CancellationToken ct) {
		var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task predecessor;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			operation.QueueBehind(_runningOperation);
			predecessor = _tail;
			_tail = released.Task;
		}

		return TakeTurnAsync(predecessor, released, operation, work, ct);
	}

	public void Dispose() {
		lock (_gate) {
			_disposed = true;
		}
	}

	private async Task<T> TakeTurnAsync<T>(
		Task predecessor,
		TaskCompletionSource released,
		MessageOperation operation,
		Func<Task<T>> work,
		CancellationToken ct) {
		try {
			await predecessor.WaitAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// Leaving the queue keeps this slot, so the operations behind still wait for what it waited for.
			_ = predecessor.ContinueWith(
				_ => released.TrySetResult(),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			throw;
		}

		lock (_gate) {
			_running = operation;
		}

		void Release() {
			lock (_gate) {
				if (_running == operation) {
					_running = null;
				}
			}

			released.TrySetResult();
		}

		if (operation.Severity == OperationSeverity.Droppable) {
			_ = operation.Expired.ContinueWith(
				_ => Release(),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}

		try {
			return await work().ConfigureAwait(false);
		} finally {
			Release();
		}
	}
}
