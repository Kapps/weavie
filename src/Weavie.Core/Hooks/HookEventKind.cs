namespace Weavie.Core.Hooks;

/// <summary>Which Claude Code hook event a <see cref="HookRequest"/> carries.</summary>
public enum HookEventKind {
	/// <summary>
	/// Fired before any tool runs (matcher <c>*</c>): change tracking snapshots the pre-edit baseline (edit
	/// tools only) and the session status flips to Working.
	/// </summary>
	PreToolUse,

	/// <summary>
	/// Fired after any tool ran (matcher <c>*</c>); carries the result. Weavie's "it actually happened" record,
	/// and the signal that an approved permission prompt resolved (status back to Working).
	/// </summary>
	PostToolUse,

	/// <summary>
	/// Fired only when a permission dialog would appear — Weavie's tool-permission gate, where the relay answers
	/// allow/deny so the prompt never shows. Unlike PreToolUse it skips always-allowed tools (Read/Grep).
	/// </summary>
	PermissionRequest,

	/// <summary>Fired when the user submits a prompt — Weavie's turn-start boundary (implicitly accepts the prior turn).</summary>
	UserPromptSubmit,

	/// <summary>Fired when Claude finishes responding — the turn-end boundary. Drives the session's Idle status.</summary>
	Stop,

	/// <summary>
	/// Fired when Claude needs attention. A permission prompt drives NeedsInput; the idle "waiting for input"
	/// notice (in <see cref="HookRequest.Message"/>) is left as-is so it neither reopens a turn nor clears a prompt.
	/// </summary>
	Notification,

	/// <summary>
	/// Fired when a conversation (re)starts; <see cref="HookRequest.Source"/> says why. Moves status Starting →
	/// Idle (claude is up); Weavie additionally watches <c>source=clear</c> to drop the resume store's stale id.
	/// </summary>
	SessionStart,

	/// <summary>Any other hook event (unrecognized name): observed, never acted on.</summary>
	Other,
}
