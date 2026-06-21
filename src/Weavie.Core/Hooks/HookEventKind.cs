namespace Weavie.Core.Hooks;

/// <summary>Which Claude Code hook event a <see cref="HookRequest"/> carries.</summary>
public enum HookEventKind {
	/// <summary>Fired before a tool runs; its decision can allow/deny/ask. Weavie's gate + record point.</summary>
	PreToolUse,

	/// <summary>Fired after a tool ran; carries the result. Weavie's "it actually happened" record.</summary>
	PostToolUse,

	/// <summary>Fired when the user submits a prompt — Weavie's turn-start boundary (implicitly accepts the prior turn).</summary>
	UserPromptSubmit,

	/// <summary>Fired when Claude finishes responding — the turn-end boundary. Drives the session's Idle status.</summary>
	Stop,

	/// <summary>Fired when Claude needs attention — a permission prompt or an idle "waiting for input" notice. Drives the session's NeedsInput status.</summary>
	Notification,

	/// <summary>
	/// Fired when a conversation (re)starts; its <see cref="HookRequest.Source"/> says why (startup / resume /
	/// clear / compact). Weavie watches <c>source=clear</c> (a <c>/clear</c>) to drop the resume store's stale id.
	/// </summary>
	SessionStart,

	/// <summary>Any other hook event (unrecognized name): observed, never acted on.</summary>
	Other,
}
