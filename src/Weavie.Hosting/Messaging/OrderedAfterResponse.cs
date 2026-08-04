namespace Weavie.Hosting.Messaging;

internal sealed class OrderedAfterResponse {
	private readonly Lock _gate = new();
	private Task _tail = Task.CompletedTask;

	public Func<CancellationToken, Task> Reserve(Func<CancellationToken, Task> work) {
		ArgumentNullException.ThrowIfNull(work);
		Task predecessor;
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) {
			predecessor = _tail;
			_tail = completed.Task;
		}

		return async ct => {
			try {
				await predecessor.ConfigureAwait(false);
				await work(ct).ConfigureAwait(false);
			} finally {
				completed.TrySetResult();
			}
		};
	}
}
