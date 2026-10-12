using System.Threading.Channels;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's serialized, coalesced refreshes of a snapshot, publishing each change.</summary>
internal sealed class CoalescedSnapshotMonitor<T>
	where T : class {
	private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(10);
	private readonly Lock _snapshotGate = new();
	private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
		FullMode = BoundedChannelFullMode.DropWrite,
		SingleReader = true,
		SingleWriter = false,
	});
	private readonly Func<CancellationToken, Task<T>> _resolve;
	private readonly Action<T> _publish;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeSpan _minimumRefreshInterval;
	private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private TaskCompletionSource _skipCooldown = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public CoalescedSnapshotMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<T>> resolve,
		Action<T> publish)
		: this(background, resolve, publish, Task.Delay, MinimumRefreshInterval) { }

	internal CoalescedSnapshotMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<T>> resolve,
		Action<T> publish,
		Func<TimeSpan, CancellationToken, Task> delay,
		TimeSpan minimumRefreshInterval) {
		ArgumentNullException.ThrowIfNull(background);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(publish);
		ArgumentNullException.ThrowIfNull(delay);
		if (minimumRefreshInterval <= TimeSpan.Zero) {
			throw new ArgumentOutOfRangeException(nameof(minimumRefreshInterval));
		}

		_resolve = resolve;
		_publish = publish;
		_delay = delay;
		_minimumRefreshInterval = minimumRefreshInterval;
		_ = background.Run(RunAsync);
	}

	public T? Latest {
		get {
			lock (_snapshotGate) {
				return field;
			}
		}

		private set;
	}

	public void RequestRefresh() => _signals.Writer.TryWrite(true);

	/// <summary>Refreshes without waiting out the cooldown, for a change the user just made rather than a burst of events.</summary>
	public void RefreshNow() {
		Volatile.Read(ref _skipCooldown).TrySetResult();
		RequestRefresh();
	}

	internal Task Waiting => _waiting.Task;

	private async Task RunAsync(CancellationToken ct) {
		_waiting.TrySetResult();
		var cooldown = Task.CompletedTask;
		while (await _signals.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) {
			await Task.WhenAny(cooldown, Volatile.Read(ref _skipCooldown).Task).ConfigureAwait(false);
			Volatile.Write(ref _skipCooldown, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
			while (_signals.Reader.TryRead(out _)) {
			}

			var snapshot = await _resolve(ct).ConfigureAwait(false);
			bool changed;
			lock (_snapshotGate) {
				changed = !Equals(Latest, snapshot);
				Latest = snapshot;
			}

			if (changed) {
				_publish(snapshot);
			}

			cooldown = _delay(_minimumRefreshInterval, ct);
		}
	}
}
