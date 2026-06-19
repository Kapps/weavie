namespace Weavie.Core.Sessions;

/// <summary>
/// The state of a session's embedded Claude, derived from its hook stream + process supervisor and shown
/// on the session switcher rail. It communicates <em>attention</em> as much as raw status — NeedsInput and
/// Error are the ones that should draw the eye. See docs/specs/multi-session-and-worktrees.md.
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

	/// <summary>Claude's process crashed or crash-looped (reported by the supervisor).</summary>
	Error,
}
