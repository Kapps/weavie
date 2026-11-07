using System.Text.Json.Serialization;
using System.Threading.Channels;
using Weavie.Core.Review;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's coalesced pull-request probes, forge reloads, and active-turn polling loop.</summary>
internal sealed class PullRequestStatusMonitor {
	private readonly Lock _snapshotGate = new();
	private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly Func<CancellationToken, Task<PullRequestStatusSnapshot>> _resolve;
	private readonly Func<PullRequestStatusSnapshot, CancellationToken, Task> _publish;
	private readonly Func<CancellationToken, Task> _reload;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeSpan _pollInterval;
	private PullRequestStatusSnapshot? _latest;

	public PullRequestStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<PullRequestStatusSnapshot>> resolve,
		Func<PullRequestStatusSnapshot, CancellationToken, Task> publish,
		Func<CancellationToken, Task> reload,
		Func<TimeSpan, CancellationToken, Task> delay,
		TimeSpan pollInterval) {
		ArgumentNullException.ThrowIfNull(background);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(publish);
		ArgumentNullException.ThrowIfNull(reload);
		ArgumentNullException.ThrowIfNull(delay);
		if (pollInterval <= TimeSpan.Zero) {
			throw new ArgumentOutOfRangeException(nameof(pollInterval));
		}

		_resolve = resolve;
		_publish = publish;
		_reload = reload;
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
		_signals.Writer.TryWrite(status is SessionStatus.Working or SessionStatus.Waiting ? Signal.Active : Signal.Idle);

	public void RequestRefresh() => _signals.Writer.TryWrite(Signal.Probe);

	/// <summary>Asks for the followed PR's comments and overview to be re-read, without re-resolving which PR it is.</summary>
	public void RequestReload() => _signals.Writer.TryWrite(Signal.Reload);

	private async Task RunAsync(CancellationToken ct) {
		bool active = false;
		while (true) {
			bool probe = false;
			bool reload = false;
			if (active && await PollElapsedBeforeSignalAsync(ct).ConfigureAwait(false)) {
				probe = true;
			} else {
				Apply(await _signals.Reader.ReadAsync(ct).ConfigureAwait(false), ref active, ref probe, ref reload);
			}

			while (_signals.Reader.TryRead(out var signal)) {
				Apply(signal, ref active, ref probe, ref reload);
			}

			if (probe) {
				var snapshot = PreserveLastGood(await _resolve(ct).ConfigureAwait(false));
				lock (_snapshotGate) {
					_latest = snapshot;
				}

				await _publish(snapshot, ct).ConfigureAwait(false);
			}

			if (reload) {
				await _reload(ct).ConfigureAwait(false);
			}
		}
	}

	private PullRequestStatusSnapshot PreserveLastGood(PullRequestStatusSnapshot snapshot) {
		lock (_snapshotGate) {
			return snapshot.Error is not null
				&& snapshot.PullRequest is null
				&& _latest?.PullRequest is { } previous
				&& string.Equals(snapshot.Branch, _latest.Branch, StringComparison.Ordinal)
					? snapshot with { PullRequest = previous, Target = _latest.Target }
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

	private static void Apply(Signal signal, ref bool active, ref bool probe, ref bool reload) {
		switch (signal) {
			case Signal.Reload:
				reload = true;
				break;
			case Signal.Probe:
				probe = true;
				break;
			default:
				probe |= active != (signal == Signal.Active);
				active = signal == Signal.Active;
				break;
		}
	}

	private enum Signal {
		Active,
		Idle,
		Probe,
		Reload,
	}
}

internal sealed record PullRequestStatusSnapshot(
	string? Branch,
	PullRequestStatusInfo? PullRequest,
	string? Error,
	[property: JsonIgnore] PullRequestTarget? Target);

internal sealed record PullRequestStatusInfo(
	int Number,
	string Url,
	string State);
