using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The update drain waits for safe sessions, quiet shells, and recent input, then freezes input and exits.
// It has no timeout; only the user's explicit restart-now overrides. See docs/specs/runner-auto-update.md.
public sealed partial class HostCore {
	// Shell foreground jobs and elapsed input grace emit no event, so a pending drain re-samples them
	// on this cadence; session status changes re-evaluate immediately via WireSession.
	private static readonly TimeSpan DrainTickInterval = TimeSpan.FromSeconds(2);
	internal static readonly TimeSpan RecentInputGrace = TimeSpan.FromMinutes(2);

	private readonly object _drainGate = new();
	private readonly Dictionary<string, long> _lastInputTimestamps = new(StringComparer.Ordinal);
	private Action? _drainExit; // non-null while a drain is in progress
	private CancellationTokenSource? _drainTick;
	private bool _drainCommitted;
	private string? _lastDrainPendingJson;
	// The authoritative input stop the page's "Updating…" overlay surfaces.
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
	/// page, and calls <paramref name="exit"/> (once) at the first safe, input-quiet moment. Idempotent —
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
			_messages.Host.Feature("updates").Publish("restarting", new { });
		} else if (pending is not null) {
			_messages.Host.Feature("updates").PublishJson("pending", pending);
		}
	}

	/// <summary>
	/// Re-checks the gate: still busy/recently active → push the holds; safe and quiet → commit. The freeze
	/// and input admission share <see cref="_drainGate"/>: either commit wins and rejects input, or input wins
	/// and renews the hold.
	/// </summary>
	private void EvaluateDrain() {
		Action? exit = null;
		lock (_drainGate) {
			if (_drainExit is null || _drainCommitted) {
				return;
			}

			long now = _drainTime.GetTimestamp();
			var holds = DrainHolds(now);
			if (holds.Count == 0) {
				_drainInputFrozen = true;
				holds = DrainHolds(now);
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

	/// <summary>Every session condition currently holding the automatic update.</summary>
	private List<(string Session, string Reason)> DrainHolds(long now) {
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

			if (_lastInputTimestamps.TryGetValue(session.SlotId, out long lastInput)
				&& _drainTime.GetElapsedTime(lastInput, now) < RecentInputGrace) {
				holds.Add((label, "recent-input"));
			}
		}

		return holds;
	}

	/// <summary>The rail label for <paramref name="session"/> (what the user sees), falling back to its slot.</summary>
	private string SlotLabelFor(HostSession session) => SlotFor(session)?.Label ?? session.SlotId;

	// Pushes the pending holds, deduped: status churn re-evaluates often and identical pushes are noise.
	private void PushDrainPendingLocked(List<(string Session, string Reason)> holds) {
		string json = JsonSerializer.Serialize(new {
			holds = holds.Select(h => new { session = h.Session, reason = h.Reason }),
		});
		if (json == _lastDrainPendingJson) {
			return;
		}

		_lastDrainPendingJson = json;
		_messages.Host.Feature("updates").PublishJson("pending", json);
	}

	private void TryAcceptInput(string slot, bool userInitiated, Action accept) {
		ArgumentNullException.ThrowIfNull(accept);
		bool reevaluate;
		lock (_drainGate) {
			if (_drainInputFrozen) {
				return;
			}

			accept();

			if (userInitiated) {
				_lastInputTimestamps[slot] = _drainTime.GetTimestamp();
			}
			reevaluate = _drainExit is not null;
		}

		if (reevaluate) {
			EvaluateDrain();
		}
	}

	private void CommitDrainRestart(Action exit) {
		_drainTick?.Cancel();
		// Persist the latest shell terminal size so the post-restart pre-spawn is born at the reattaching xterm's width.
		_sessionStore.Flush();
		// Best-effort heads-up; the page also shows the overlay when the socket drops mid-drain, so a
		// push lost to the shutdown race still surfaces.
		_messages.Host.Feature("updates").Publish("restarting", new { });
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

	internal void EvaluateDrainForTest() => EvaluateDrain();
}
