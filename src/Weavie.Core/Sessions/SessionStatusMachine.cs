using Weavie.Core.Hooks;
using Weavie.Core.Processes;

namespace Weavie.Core.Sessions;

/// <summary>
/// Derives a session's <see cref="SessionStatus"/> from the embedded Claude's hook stream and its
/// <see cref="ProcessSupervisor"/> state. Thread-safe: hook and supervisor events arrive on different
/// threads, so handlers of <see cref="Changed"/> must marshal to the UI thread before rendering.
/// </summary>
public sealed class SessionStatusMachine {
	private readonly Lock _gate = new();
	private SessionStatus _status = SessionStatus.Starting;

	/// <summary>Raised (off the UI thread) when the status changes, carrying the new status.</summary>
	public event Action<SessionStatus>? Changed;

	/// <summary>The current status.</summary>
	public SessionStatus Status {
		get {
			lock (_gate) {
				return _status;
			}
		}
	}

	/// <summary>Feeds a hook event into the machine — wire to <see cref="HookBridgeServer.Observed"/>.</summary>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		var next = request.Event switch {
			HookEventKind.UserPromptSubmit => SessionStatus.Working,
			HookEventKind.PreToolUse => SessionStatus.Working,
			HookEventKind.PostToolUse => SessionStatus.Working,
			HookEventKind.Notification => ClassifyNotification(request),
			HookEventKind.Stop => SessionStatus.Idle,
			// First hook of a fresh/resumed/cleared conversation: claude is up and waiting for input, so leave
			// Starting for the calm green Idle. (A mid-turn compact also fires SessionStart; the next tool call
			// re-arms Working, so the brief Idle is harmless.)
			HookEventKind.SessionStart => SessionStatus.Idle,
			_ => null,
		};
		if (next is { } status) {
			Set(status);
		}
	}

	/// <summary>
	/// Maps a Notification to a status. Claude fires it both for a permission prompt (the user must act →
	/// NeedsInput) and for the idle "waiting for your input" notice it emits once a turn has settled. The idle
	/// notice must NOT change status: it arrives right after <see cref="HookEventKind.Stop"/> (so treating it as
	/// NeedsInput would flip a finished turn back to orange), and it can also fire while a permission prompt is
	/// still open (so it must not clear a genuine NeedsInput). Returning null leaves the resting state intact.
	/// </summary>
	private static SessionStatus? ClassifyNotification(HookRequest request) =>
		request.Message is { } message && message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase)
			? null
			: SessionStatus.NeedsInput;

	/// <summary>
	/// Feeds a supervisor transition for the session's Claude process — wire to
	/// <see cref="ProcessSupervisor.StateChanged"/>. A crash-loop or crash awaiting restart becomes Error; a
	/// post-crash restart becomes Starting until the new process produces hooks.
	/// </summary>
	public void ObserveSupervisor(SupervisorStateChanged change) {
		SessionStatus? next = change.State switch {
			SupervisorState.Failed => SessionStatus.Error,
			SupervisorState.BackingOff when change.ExitCode is not null => SessionStatus.Error,
			SupervisorState.Running when change.RestartCount > 0 => SessionStatus.Starting,
			_ => null,
		};
		if (next is { } status) {
			Set(status);
		}
	}

	private void Set(SessionStatus next) {
		bool changed;
		lock (_gate) {
			changed = _status != next;
			if (changed) {
				_status = next;
			}
		}

		if (changed) {
			Changed?.Invoke(next);
		}
	}
}
