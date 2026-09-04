namespace Weavie.Hosting;

internal sealed class SessionTaskScope : IAsyncDisposable {
	private readonly object _gate = new();
	private readonly HashSet<Task> _tasks = [];
	private readonly CancellationTokenSource _stopping = new();
	private readonly Action<string> _log;
	private readonly TaskCompletionSource _stopped =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _closed;

	public SessionTaskScope(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
		Stopping = _stopping.Token;
	}

	public CancellationToken Stopping { get; }

	/// <summary>
	/// Starts <paramref name="work"/> in this scope, returning its task — or <see langword="null"/> when the scope
	/// is closed (the session is unloading) and admits nothing. Nothing runs in that case, so a caller holding a
	/// resource for the work — a claimed slot, an open spinner — must release it rather than assume the work will.
	/// </summary>
	public Task? Run(Func<CancellationToken, Task> work) {
		ArgumentNullException.ThrowIfNull(work);
		var admitted = new TaskCompletionSource();
		Task task;
		lock (_gate) {
			if (_closed) {
				return null;
			}

			task = RunCoreAsync(work, admitted.Task);
			_tasks.Add(task);
		}

		_ = task.ContinueWith(
			(_, state) => {
				var scope = (SessionTaskScope)state!;
				lock (scope._gate) {
					scope._tasks.Remove(task);
				}
			},
			this,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
		admitted.SetResult();
		return task;
	}

	public ValueTask DisposeAsync() {
		Task[]? tasks = null;
		lock (_gate) {
			if (!_closed) {
				_closed = true;
				tasks = [.. _tasks];
			}
		}

		if (tasks is not null) {
			_ = StopAsync(tasks);
		}

		return new ValueTask(_stopped.Task);
	}

	private async Task RunCoreAsync(Func<CancellationToken, Task> work, Task admitted) {
		try {
			await admitted.ConfigureAwait(false);
			await work(_stopping.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (_stopping.IsCancellationRequested) {
		} catch (Exception ex) {
			_log($"session background work failed: {ex}");
		}
	}

	private async Task StopAsync(Task[] tasks) {
		try {
			_stopping.Cancel();
			await Task.WhenAll(tasks).ConfigureAwait(false);
			_stopped.TrySetResult();
		} catch (Exception ex) {
			_stopped.TrySetException(ex);
		} finally {
			_stopping.Dispose();
		}
	}
}
