namespace Weavie.Core.Sessions;

/// <summary>
/// The state of a session's embedded Claude, derived from its hook stream + process supervisor and shown on
/// the session switcher rail. See docs/specs/multi-session-and-worktrees.md.
/// </summary>
public enum SessionStatus {
	/// <summary>Claude is launching and hasn't produced its first hook event yet.</summary>
	Starting,

	/// <summary>A turn is in progress — Claude is working (prompt submitted / tools running).</summary>
	Working,

	/// <summary>Claude needs the user: a permission prompt or an idle "waiting for input" notice.</summary>
	NeedsInput,

	/// <summary>The last turn ended; Claude is idle and waiting (calm — fades to neutral in the UI).</summary>
	Idle,

	/// <summary>
	/// The last turn ended, but the session will resume itself: it armed a self-continuation (a scheduled
	/// wakeup or a detached background task) that hasn't fired yet. Idle to the eye, but not done — the update
	/// drain holds for it so an auto-update never restarts away a pending overnight step. See
	/// docs/specs/runner-auto-update.md.
	/// </summary>
	Waiting,

	/// <summary>Claude's process crashed or crash-looped (reported by the supervisor).</summary>
	Error,
}
