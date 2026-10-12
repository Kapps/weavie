namespace Weavie.Core.Processes;

// The observer lane: notifications are delivered in queue order on one thread-pool worker at a time, so a slow or
// throwing handler can never block or break supervision.
public sealed partial class ProcessSupervisor {
	private readonly Queue<Action> _notifications = [];
	private TaskCompletionSource _notificationIdle = CompletedNotification();
	private int _stateNotificationsClosed;
	private bool _notificationRunning;

	/// <summary>Raised in transition order; nothing is raised once the supervisor is disposed.</summary>
	public event Action<SupervisorStateChanged>? StateChanged;

	/// <summary>Raised when a stop had to force-kill its child, including the stop that disposal runs.</summary>
	public event Action<ProcessStopOutcome>? Stopped;

	internal Task DrainObserversAsync() {
		lock (_gate) {
			return _notificationIdle.Task;
		}
	}

	private void CloseStateNotificationsLocked() => Volatile.Write(ref _stateNotificationsClosed, 1);

	private void QueueStateChangedLocked(SupervisorStateChanged change) {
		if (Volatile.Read(ref _stateNotificationsClosed) == 0) {
			QueueLocked(StateChanged, () => StateChanged, change, closable: true);
		}
	}

	private void QueueStoppedLocked(ProcessStopOutcome outcome) =>
		QueueLocked(Stopped, () => Stopped, outcome, closable: false);

	private void QueueLocked<T>(Action<T>? handlers, Func<Action<T>?> subscribed, T value, bool closable) {
		if (handlers is null) {
			return;
		}

		_notifications.Enqueue(() => Deliver(handlers, subscribed, value, closable));
		if (_notificationRunning) {
			return;
		}

		_notificationRunning = true;
		_notificationIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		ThreadPool.UnsafeQueueUserWorkItem(static supervisor => supervisor.DrainNotifications(), this, preferLocal: false);
	}

	private void DrainNotifications() {
		while (true) {
			Action notification;
			lock (_gate) {
				if (_notifications.Count == 0) {
					_notificationRunning = false;
					_notificationIdle.TrySetResult();
					return;
				}

				notification = _notifications.Dequeue();
			}

			notification();
		}
	}

	// A handler unsubscribed after queueing is skipped, as is every state change once disposal closed the lane.
	private void Deliver<T>(Action<T> handlers, Func<Action<T>?> subscribed, T value, bool closable) {
		foreach (Action<T> handler in handlers.GetInvocationList()) {
			if ((closable && Volatile.Read(ref _stateNotificationsClosed) != 0)
				|| subscribed()?.GetInvocationList().Contains(handler) != true) {
				continue;
			}

			try {
				handler(value);
			} catch (Exception) {
			}
		}
	}

	private static TaskCompletionSource CompletedNotification() {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		completion.SetResult();
		return completion;
	}
}
