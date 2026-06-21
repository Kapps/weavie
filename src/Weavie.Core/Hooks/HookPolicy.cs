namespace Weavie.Core.Hooks;

/// <summary>
/// Weavie's tool-permission gate, orthogonal to Claude's own edit mode (which Weavie only observes — see
/// <see cref="ObservedPermissionMode"/>). Driven by the <c>claude.allowAllTools</c> setting: when off, every
/// request passes through to Claude's normal flow; when on, every PermissionRequest for a non-edit tool is
/// auto-allowed (no <c>--dangerously-skip-permissions</c> needed). Edits always pass through so Claude's edit
/// mode governs them — a hook <c>allow</c> would override it (hook &gt; ask). A user <c>deny</c> rule still wins.
/// </summary>
public static class HookPolicy {
	/// <summary>Returns the bridge's verdict for <paramref name="request"/> given the <c>claude.allowAllTools</c> setting.</summary>
	/// <param name="request">The observed hook event.</param>
	/// <param name="allowAllTools">Whether Weavie auto-allows non-edit tool calls (the <c>claude.allowAllTools</c> setting).</param>
	public static HookDecision Decide(HookRequest request, bool allowAllTools) {
		ArgumentNullException.ThrowIfNull(request);
		if (allowAllTools
			&& request.Event == HookEventKind.PermissionRequest
			&& !IsEditTool(request.ToolName)) {
			return HookDecision.Allow("Weavie allow-all-tools");
		}

		return HookDecision.PassThrough;
	}

	/// <summary>The mutating edit tools, whose permission is governed by Claude's edit mode — never auto-allowed here.</summary>
	private static bool IsEditTool(string toolName) =>
		toolName is "Edit" or "Write" or "MultiEdit" or "NotebookEdit";
}
