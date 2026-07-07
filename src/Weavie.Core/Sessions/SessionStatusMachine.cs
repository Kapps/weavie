using Weavie.Core.Agents;
using Weavie.Core.Processes;

namespace Weavie.Core.Sessions;

/// <summary>
/// Derives a session's <see cref="SessionStatus"/> from the embedded Claude's hook stream (events + gate
/// decisions) and its <see cref="ProcessSupervisor"/> state. Thread-safe: hook and supervisor events arrive on
/// different threads, so handlers of <see cref="Changed"/> must marshal to the UI thread before rendering.
/// </summary>
public sealed class SessionStatusMachine {
	private readonly Lock _gate = new();
	private readonly Lock _deliveryGate = new();
	private SessionStatus _status = SessionStatus.Starting;
	private long _version;
	private long _deliveredVersion;

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

	/// <summary>Feeds a normalized agent event into the machine.</summary>
	public void Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		Apply(current => value switch {
			AgentPromptSubmitted => SessionStatus.Working,
			AgentToolStarting => SessionStatus.Working,
			// The tool ran — in particular, the user approved its permission prompt — so the turn is live again.
			AgentToolCompleted => SessionStatus.Working,
			AgentNotification notification => ClassifyNotification(notification, current),
			// A turn ending with a pending wakeup / in-flight background task is idle-but-not-done: it will
			// resume itself, so it settles to Waiting (which holds the update drain) rather than Idle.
			AgentTurnStopped stopped => stopped.WillResume ? SessionStatus.Waiting : SessionStatus.Idle,
			// A mid-turn auto-compact also fires SessionStart (source=compact); only the other sources
			// (startup/resume/clear) mean claude is up and waiting.
			AgentSessionStarted started when started.Source != "compact" => SessionStatus.Idle,
			AgentPermissionResolved permission => permission.RequiresUserInput
				? SessionStatus.NeedsInput
				: SessionStatus.Working,
			_ => null,
		});
	}

	/// <summary>
	/// Feeds the user's keystrokes into the claude pane — wire to the claude terminal's input stream. No hook
	/// fires when a permission prompt is answered (the approved tool only reports back at PostToolUse, which for
	/// a long build is minutes later), but answering IS typing, so it resolves NeedsInput to Working. Anything
	/// ESC-prefixed except the bare Esc key never answers a dialog — arrows/mouse/focus reports, Alt chords, and
	/// the terminal's automatic OSC/DCS query replies — so only a lone 0x1b or a plain chunk counts. The hook
	/// stream corrects the guess within one event either way.
	/// </summary>
	public void ObserveUserInput(byte[] data) {
		ArgumentNullException.ThrowIfNull(data);
		if (data.Length == 0 || (data[0] == 0x1b && data.Length > 1)) {
			return;
		}

		Apply(current => current == SessionStatus.NeedsInput ? SessionStatus.Working : null);
	}

	/// <summary>
	/// Maps a Notification to a status. A permission prompt becomes NeedsInput. The idle "waiting for your
	/// input" notice depends on where the session rests: from Working it means the turn ended without a Stop
	/// (the user interrupted), so it settles to Idle; from NeedsInput or Idle it changes nothing (it also fires
	/// right after Stop and while a prompt is open, and must not disturb those states).
	/// </summary>
	private static SessionStatus? ClassifyNotification(AgentNotification notification, SessionStatus current) {
		if (notification.Message is not { } message
			|| !message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase)) {
			return SessionStatus.NeedsInput;
		}

		return current is SessionStatus.Working or SessionStatus.Starting ? SessionStatus.Idle : null;
	}

	/// <summary>
	/// Feeds a supervisor transition for the session's Claude process — wire to
	/// <see cref="ProcessSupervisor.StateChanged"/>. A crash becomes Error; a post-crash restart becomes Starting.
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

	private void Set(SessionStatus next) => Apply(_ => next);

	// Runs the transition and the state swap under one lock, so a state-dependent rule (the idle notice) can't
	// race a concurrent event between reading the status and writing its result. Delivery is version-stamped:
	// when two transitions race (hook vs. supervisor vs. input threads), a notification that lost the race is
	// dropped instead of delivered after the newer one, so handlers never end on a stale status.
	private void Apply(Func<SessionStatus, SessionStatus?> transition) {
		SessionStatus next;
		long version;
		lock (_gate) {
			if (transition(_status) is not { } candidate || candidate == _status) {
				return;
			}

			_status = candidate;
			next = candidate;
			version = ++_version;
		}

		lock (_deliveryGate) {
			if (version <= _deliveredVersion) {
				return;
			}

			_deliveredVersion = version;
			Changed?.Invoke(next);
		}
	}
}
