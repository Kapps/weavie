using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The update drain gate: BeginDrain holds until no session is Working/NeedsInput/Waiting (a pending
// scheduled wakeup or background task) and no shell pane runs a foreground job, then freezes terminal
// input, tells the page it's restarting, and invokes
// the exit callback. There is deliberately NO drain timeout — a busy box holds the update until
// quiet, and only the user's explicit restart-now overrides. See docs/specs/runner-auto-update.md.
public sealed partial class HostCore {
	// Shell foreground jobs emit no event, so a pending drain re-samples them (and everything else)
	// on this cadence; session status changes re-evaluate immediately via WireSession.
	private static readonly TimeSpan DrainTickInterval = TimeSpan.FromSeconds(2);

	private readonly object _drainGate = new();
	private Action? _drainExit; // non-null while a drain is in progress
	private CancellationTokenSource? _drainTick;
	private bool _drainCommitted;
	private string? _lastDrainPendingJson;
	// Checked by the term-input dispatch: once a restart commits, no further keystrokes reach any
	// PTY — the authoritative input stop the page's "Updating…" overlay surfaces.
	private volatile bool _drainInputFrozen;

	/// <summary>Whether an update drain is in progress (waiting for quiet, or already committed).</summary>
	public bool Draining {
		get {
			lock (_drainGate) {
				return _drainExit is not null;
			}
		}
	}

	/// <summary>
	/// Begins draining for an update restart: the core keeps serving normally, pushes the holds to the
	/// page, and calls <paramref name="exit"/> (once) at the first moment nothing is busy. Idempotent —
	/// a second call while draining is the same drain (the staged build only got newer).
	/// </summary>
	public void BeginDrain(Action exit) {
		ArgumentNullException.ThrowIfNull(exit);
		lock (_drainGate) {
			if (_drainExit is not null) {
				return;
			}

			_drainExit = exit;
			var tick = new CancellationTokenSource();
			_drainTick = tick;
			_ = Task.Run(() => DrainTickLoopAsync(tick.Token));
		}

		Log("[weavie] update drain started");
		EvaluateDrain();
	}

	/// <summary>
	/// The user's explicit restart-now: skips the gate and restarts immediately, killing any running
	/// shell jobs — never taken automatically. Fails when no update restart is pending.
	/// </summary>
	public CommandResult RestartNowForUpdate() {
		Action exit;
		lock (_drainGate) {
			if (_drainExit is not { } pendingExit) {
				return CommandResult.Failure("No update is pending, so there's nothing to restart into.");
			}

			if (_drainCommitted) {
				return CommandResult.Success("Already restarting for the update.");
			}

			_drainInputFrozen = true;
			_drainCommitted = true;
			exit = pendingExit;
		}

		CommitDrainRestart(exit);
		return CommandResult.Success("Restarting now to apply the update.");
	}

	/// <summary>Re-pushes the current drain state (the page just [re]connected mid-drain); no-op otherwise.</summary>
	private void PushDrainStateToWeb() {
		string? pending;
		bool committed;
		lock (_drainGate) {
			if (_drainExit is null) {
				return;
			}

			pending = _lastDrainPendingJson;
			committed = _drainCommitted;
		}

		if (committed) {
			_bridge.PostToWeb("{\"type\":\"update-restarting\"}");
		} else if (pending is not null) {
			_bridge.PostToWeb(pending);
		}
	}

	/// <summary>
	/// Re-checks the gate: still busy → push the holds; quiet → commit. Committing freezes input FIRST
	/// and re-checks once more, because a prompt already in flight can flip a session Working between
	/// the check and the freeze (Working is only set when its hook arrives) — the residual race after
	/// the freeze is bounded by that hook latency. Called on any session status change and on the tick.
	/// </summary>
	private void EvaluateDrain() {
		Action? exit = null;
		lock (_drainGate) {
			if (_drainExit is null || _drainCommitted) {
				return;
			}

			var holds = DrainHolds();
			if (holds.Count == 0) {
				_drainInputFrozen = true;
				holds = DrainHolds();
				if (holds.Count == 0) {
					_drainCommitted = true;
					exit = _drainExit;
				} else {
					_drainInputFrozen = false;
				}
			}

			if (exit is null) {
				PushDrainPendingLocked(holds);
				return;
			}
		}

		CommitDrainRestart(exit);
	}

	/// <summary>
	/// What's holding the drain: each loaded session that is Working / awaiting a permission answer,
	/// and each shell pane with a foreground job (killing one unattended would be silent destruction;
	/// background jobs are invisible to the probe and die at restart — the page says so).
	/// </summary>
	private List<(string Session, string Reason)> DrainHolds() {
		var holds = new List<(string, string)>();
		foreach (var session in LoadedSessions()) {
			string label = SlotLabelFor(session);
			switch (session.Status.Status) {
				case SessionStatus.Working:
					holds.Add((label, "working"));
					break;
				case SessionStatus.NeedsInput:
					holds.Add((label, "needs-input"));
					break;
				// Idle to the eye, but a scheduled wakeup / background task is pending — restarting would kill it.
				case SessionStatus.Waiting:
					holds.Add((label, "waiting-on-task"));
					break;
				default:
					break;
			}

			if (session.Shell.HasForegroundJob) {
				holds.Add((label, "shell-job"));
			}
		}

		return holds;
	}

	/// <summary>The rail label for <paramref name="session"/> (what the user sees), falling back to its id.</summary>
	private string SlotLabelFor(HostSession session) =>
		_sessions?.Slots.FirstOrDefault(slot => ReferenceEquals(slot.Session, session))?.Label ?? session.Id;

	// Pushes the pending holds, deduped: status churn re-evaluates often and identical pushes are noise.
	private void PushDrainPendingLocked(List<(string Session, string Reason)> holds) {
		string json = JsonSerializer.Serialize(new {
			type = "update-pending",
			holds = holds.Select(h => new { session = h.Session, reason = h.Reason }),
		});
		if (json == _lastDrainPendingJson) {
			return;
		}

		_lastDrainPendingJson = json;
		_bridge.PostToWeb(json);
	}

	private void CommitDrainRestart(Action exit) {
		_drainTick?.Cancel();
		// Best-effort heads-up; the page also shows the overlay when the socket drops mid-drain, so a
		// push lost to the shutdown race still surfaces.
		_bridge.PostToWeb("{\"type\":\"update-restarting\"}");
		Log("[weavie] update drain complete - restarting");
		exit();
	}

	private async Task DrainTickLoopAsync(CancellationToken ct) {
		try {
			while (!ct.IsCancellationRequested) {
				await Task.Delay(DrainTickInterval, ct).ConfigureAwait(false);
				EvaluateDrain();
			}
		} catch (OperationCanceledException) {
			// Commit or dispose ended the drain.
		}
	}
}
