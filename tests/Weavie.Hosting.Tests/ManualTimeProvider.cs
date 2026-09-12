namespace Weavie.Hosting.Tests;

internal sealed class ManualTimeProvider : TimeProvider {
	private readonly object _gate = new();
	private readonly List<ManualTimer> _timers = [];
	private long _timestamp;

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
		var timer = new ManualTimer(this, callback, state);
		timer.Change(dueTime, period);
		return timer;
	}

	/// Moves the clock and fires every timer that became due, on the thread pool as a real timer would.
	public void Advance(TimeSpan duration) {
		long now = Interlocked.Add(ref _timestamp, duration.Ticks);
		List<ManualTimer> due;
		lock (_gate) {
			due = [.. _timers.Where(timer => timer.DueAt <= now)];
			foreach (var timer in due) {
				timer.Rearm(now);
			}
		}

		foreach (var timer in due) {
			timer.Fire();
		}
	}

	private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer {
		private long _period;

		public long DueAt { get; private set; } = long.MaxValue;

		public bool Change(TimeSpan dueTime, TimeSpan period) {
			lock (owner._gate) {
				DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
				_period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
				if (!owner._timers.Contains(this)) {
					owner._timers.Add(this);
				}
			}

			return true;
		}

		public void Rearm(long now) => DueAt = _period > 0 ? now + _period : long.MaxValue;

		public void Fire() => Task.Run(() => callback(state));

		public void Dispose() {
			lock (owner._gate) {
				owner._timers.Remove(this);
			}
		}

		public ValueTask DisposeAsync() {
			Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
