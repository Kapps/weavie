using Weavie.Core.Processes;

namespace Weavie.Core.Tests;

/// <summary>A clock whose delays complete only when the test advances time past their due point.</summary>
internal sealed class FakeSupervisorClock : ISupervisorClock {
	private readonly object _gate = new();
	private readonly List<Pending> _pending = [];
	private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

	public DateTimeOffset UtcNow {
		get {
			lock (_gate) {
				return _now;
			}
		}
	}

	public Task Delay(TimeSpan delay, CancellationToken cancellationToken) {
		if (delay <= TimeSpan.Zero) {
			return Task.CompletedTask;
		}

		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) {
			_pending.Add(new Pending(_now + delay, tcs));
		}

		cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
		return tcs.Task;
	}

	public void Advance(TimeSpan by) {
		var due = new List<TaskCompletionSource>();
		lock (_gate) {
			_now += by;
			for (int i = _pending.Count - 1; i >= 0; i--) {
				if (_pending[i].Due <= _now) {
					due.Add(_pending[i].Tcs);
					_pending.RemoveAt(i);
				}
			}
		}

		foreach (var tcs in due) {
			tcs.TrySetResult();
		}
	}

	private readonly record struct Pending(DateTimeOffset Due, TaskCompletionSource Tcs);
}
