using System.Text.Json.Serialization;
using System.Threading.Channels;
using Weavie.Core.Review;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's coalesced pull-request probes, forge reloads, and polling loop.</summary>
internal sealed class PullRequestStatusMonitor {
	private readonly Lock _snapshotGate = new();
	private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly Func<CancellationToken, Task<PullRequestStatusSnapshot>> _resolve;
	private readonly Func<PullRequestStatusSnapshot, CancellationToken, Task> _publish;
	private readonly Func<CancellationToken, Task> _reload;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly PullRequestPollIntervals _intervals;
	private PullRequestStatusSnapshot? _latest;

	public PullRequestStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<PullRequestStatusSnapshot>> resolve,
		Func<PullRequestStatusSnapshot, CancellationToken, Task> publish,
		Func<CancellationToken, Task> reload,
		Func<TimeSpan, CancellationToken, Task> delay,
		PullRequestPollIntervals intervals) {
		ArgumentNullException.ThrowIfNull(background);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(publish);
		ArgumentNullException.ThrowIfNull(reload);
		ArgumentNullException.ThrowIfNull(delay);
		ArgumentNullException.ThrowIfNull(intervals);
		if (intervals.Active <= TimeSpan.Zero || intervals.QuietOpen <= TimeSpan.Zero) {
			throw new ArgumentOutOfRangeException(nameof(intervals));
		}

		_resolve = resolve;
		_publish = publish;
		_reload = reload;
		_delay = delay;
		_intervals = intervals;
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
			if (PollInterval(active) is { } interval && await PollElapsedBeforeSignalAsync(interval, ct).ConfigureAwait(false)) {
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

	// A quiet session's open pull request can still be merged or closed elsewhere, so it keeps a slow poll.
	private TimeSpan? PollInterval(bool active) =>
		active ? _intervals.Active
		: Latest?.PullRequest is { State: PullRequestStatusInfo.Open } ? _intervals.QuietOpen
		: null;

	private async Task<bool> PollElapsedBeforeSignalAsync(TimeSpan interval, CancellationToken ct) {
		using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task signal = _signals.Reader.WaitToReadAsync(race.Token).AsTask();
		var poll = _delay(interval, race.Token);
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
	string State) {
	/// <summary>The <see cref="State"/> of a pull request that can still change.</summary>
	public const string Open = "open";
}

/// <summary>How often a session's pull request is re-probed: during a turn, and while quiet with an open pull request.</summary>
internal sealed record PullRequestPollIntervals(TimeSpan Active, TimeSpan QuietOpen);
