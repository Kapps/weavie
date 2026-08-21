using System.Threading.Channels;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's serialized, coalesced Git-status refreshes.</summary>
internal sealed class GitStatusMonitor {
	private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(10);
	private readonly Lock _snapshotGate = new();
	private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
		FullMode = BoundedChannelFullMode.DropWrite,
		SingleReader = true,
		SingleWriter = false,
	});
	private readonly Func<CancellationToken, Task<GitStatusSnapshot>> _resolve;
	private readonly Action<GitStatusSnapshot> _publish;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeSpan _minimumRefreshInterval;
	private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public GitStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<GitStatusSnapshot>> resolve,
		Action<GitStatusSnapshot> publish)
		: this(background, resolve, publish, Task.Delay, MinimumRefreshInterval) { }

	internal GitStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<GitStatusSnapshot>> resolve,
		Action<GitStatusSnapshot> publish,
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

	public GitStatusSnapshot? Latest {
		get {
			lock (_snapshotGate) {
				return field;
			}
		}

		private set;
	}

	public void RequestRefresh() => _signals.Writer.TryWrite(true);

	internal Task Waiting => _waiting.Task;

	private async Task RunAsync(CancellationToken ct) {
		_waiting.TrySetResult();
		var cooldown = Task.CompletedTask;
		while (await _signals.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) {
			await cooldown.ConfigureAwait(false);
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

internal sealed record GitStatusSnapshot(
	string? Branch,
	bool Dirty,
	int? Added,
	int? Removed,
	string? Error);
