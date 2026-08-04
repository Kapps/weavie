using System.Threading.Channels;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's serialized, coalesced Git-status refreshes.</summary>
internal sealed class GitStatusMonitor {
	private readonly Lock _snapshotGate = new();
	private readonly Channel<bool> _signals = Channel.CreateUnbounded<bool>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly Func<CancellationToken, Task<GitStatusSnapshot>> _resolve;
	private readonly Action<GitStatusSnapshot> _publish;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeSpan _pollInterval;

	public GitStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<GitStatusSnapshot>> resolve,
		Action<GitStatusSnapshot> publish,
		Func<TimeSpan, CancellationToken, Task> delay,
		TimeSpan pollInterval) {
		ArgumentNullException.ThrowIfNull(background);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(publish);
		ArgumentNullException.ThrowIfNull(delay);
		if (pollInterval <= TimeSpan.Zero) {
			throw new ArgumentOutOfRangeException(nameof(pollInterval));
		}

		_resolve = resolve;
		_publish = publish;
		_delay = delay;
		_pollInterval = pollInterval;
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

	private async Task RunAsync(CancellationToken ct) {
		while (true) {
			await WaitForRefreshAsync(ct).ConfigureAwait(false);
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
		}
	}

	private async Task WaitForRefreshAsync(CancellationToken ct) {
		using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task signal = _signals.Reader.WaitToReadAsync(race.Token).AsTask();
		var poll = _delay(_pollInterval, race.Token);
		var winner = await Task.WhenAny(signal, poll).ConfigureAwait(false);
		await winner.ConfigureAwait(false);
		race.Cancel();
		try {
			await (ReferenceEquals(winner, signal) ? poll : signal).ConfigureAwait(false);
		} catch (OperationCanceledException) when (race.IsCancellationRequested) {
		}
	}
}

internal sealed record GitStatusSnapshot(
	string? Branch,
	bool Dirty,
	int? Added,
	int? Removed,
	string? Error);
