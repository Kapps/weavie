namespace Weavie.Core.Hooks;

/// <summary>Which Claude Code hook event a <see cref="HookRequest"/> carries.</summary>
public enum HookEventKind {
	/// <summary>Fired before a tool runs; its decision can allow/deny/ask. Weavie's gate + record point.</summary>
	PreToolUse,

	/// <summary>Fired after a tool ran; carries the result. Weavie's "it actually happened" record.</summary>
	PostToolUse,

	/// <summary>Any other hook event (unrecognized name): observed, never acted on.</summary>
	Other,
}
