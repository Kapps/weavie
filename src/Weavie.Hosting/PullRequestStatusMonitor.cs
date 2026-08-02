using System.Threading.Channels;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's coalesced pull-request refresh and active-turn polling loop.</summary>
internal sealed class PullRequestStatusMonitor {
	private readonly Lock _snapshotGate = new();
	private readonly Channel<bool?> _signals = Channel.CreateUnbounded<bool?>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly Func<CancellationToken, Task<PullRequestStatusSnapshot>> _resolve;
	private readonly Action<PullRequestStatusSnapshot> _publish;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeSpan _pollInterval;
	private PullRequestStatusSnapshot? _latest;

	public PullRequestStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<PullRequestStatusSnapshot>> resolve,
		Action<PullRequestStatusSnapshot> publish,
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

	public PullRequestStatusSnapshot? Latest {
		get {
			lock (_snapshotGate) {
				return _latest;
			}
		}
	}

	public void UpdateStatus(SessionStatus status) =>
		_signals.Writer.TryWrite(status is SessionStatus.Working or SessionStatus.Waiting);

	public void RequestRefresh() => _signals.Writer.TryWrite(null);

	private async Task RunAsync(CancellationToken ct) {
		bool active = false;
		bool refresh = false;
		while (true) {
			if (!refresh) {
				if (active && await PollElapsedBeforeSignalAsync(ct).ConfigureAwait(false)) {
					refresh = true;
				} else {
					Apply(await _signals.Reader.ReadAsync(ct).ConfigureAwait(false), ref active, ref refresh);
				}
			}

			while (_signals.Reader.TryRead(out bool? signal)) {
				Apply(signal, ref active, ref refresh);
			}

			if (!refresh) {
				continue;
			}

			refresh = false;
			var snapshot = PreserveLastGood(await _resolve(ct).ConfigureAwait(false));
			lock (_snapshotGate) {
				_latest = snapshot;
			}

			_publish(snapshot);
		}
	}

	private PullRequestStatusSnapshot PreserveLastGood(PullRequestStatusSnapshot snapshot) {
		lock (_snapshotGate) {
			return snapshot.Error is not null
				&& snapshot.PullRequest is null
				&& _latest?.PullRequest is { } previous
				&& string.Equals(snapshot.Branch, _latest.Branch, StringComparison.Ordinal)
					? snapshot with { PullRequest = previous }
					: snapshot;
		}
	}

	private async Task<bool> PollElapsedBeforeSignalAsync(CancellationToken ct) {
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

		return ReferenceEquals(winner, poll);
	}

	private static void Apply(bool? signal, ref bool active, ref bool refresh) {
		if (signal is { } nextActive) {
			if (nextActive != active) {
				active = nextActive;
				refresh = true;
			}
		} else {
			refresh = true;
		}
	}
}

internal sealed record PullRequestStatusSnapshot(
	string? Branch,
	PullRequestStatusInfo? PullRequest,
	string? Error);

internal sealed record PullRequestStatusInfo(
	int Number,
	string Url,
	string State);
