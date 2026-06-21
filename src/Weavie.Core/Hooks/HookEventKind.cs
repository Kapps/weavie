namespace Weavie.Core.Hooks;

/// <summary>Which Claude Code hook event a <see cref="HookRequest"/> carries.</summary>
public enum HookEventKind {
	/// <summary>Fired before a tool runs; Weavie observes it to snapshot the pre-edit baseline for change tracking.</summary>
	PreToolUse,

	/// <summary>Fired after a tool ran; carries the result. Weavie's "it actually happened" record.</summary>
	PostToolUse,

	/// <summary>
	/// Fired only when a permission dialog would appear (a tool not already allowed). Weavie's tool-permission
	/// gate: the relay answers allow/deny here so the prompt never shows. Unlike PreToolUse it does not fire on
	/// every tool call, so always-allowed tools (Read/Grep) cost nothing.
	/// </summary>
	PermissionRequest,

	/// <summary>Fired when the user submits a prompt — Weavie's turn-start boundary (implicitly accepts the prior turn).</summary>
	UserPromptSubmit,

	/// <summary>Fired when Claude finishes responding — the turn-end boundary. Drives the session's Idle status.</summary>
	Stop,

	/// <summary>Fired when Claude needs attention — a permission prompt or an idle "waiting for input" notice. Drives the session's NeedsInput status.</summary>
	Notification,

	/// <summary>
	/// Fired when a conversation (re)starts; <see cref="HookRequest.Source"/> says why (startup/resume/clear/
	/// compact). Weavie watches <c>source=clear</c> to drop the resume store's stale id.
	/// </summary>
	SessionStart,

	/// <summary>Any other hook event (unrecognized name): observed, never acted on.</summary>
	Other,
}
