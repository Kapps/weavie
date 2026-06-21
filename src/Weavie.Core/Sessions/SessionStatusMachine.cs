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
		SessionStatus? next = request.Event switch {
			HookEventKind.UserPromptSubmit => SessionStatus.Working,
			HookEventKind.PreToolUse => SessionStatus.Working,
			HookEventKind.PostToolUse => SessionStatus.Working,
			HookEventKind.Notification => SessionStatus.NeedsInput,
			HookEventKind.Stop => SessionStatus.Idle,
			_ => null,
		};
		if (next is { } status) {
			Set(status);
		}
	}

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
