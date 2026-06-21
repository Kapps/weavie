namespace Weavie.Core.Hooks;

/// <summary>
/// Weavie's tool-permission gate — the axis Weavie owns, orthogonal to Claude's own edit mode (which Weavie
/// only observes, see <see cref="ObservedPermissionMode"/>). Driven by the <c>claude.allowAllTools</c> setting:
/// <list type="bullet">
/// <item><b>allowAllTools off</b> — <see cref="HookDecision.PassThrough"/>: defer to Claude's normal flow
/// (edits → its mode / the openDiff presenter; Bash → its own terminal prompt).</item>
/// <item><b>allowAllTools on</b> — <see cref="HookDecision.Allow"/> every PermissionRequest for a NON-edit tool
/// (Bash &amp; other commands): Weavie auto-answers those permission prompts, no <c>--dangerously-skip-permissions</c>
/// needed. Edits are deliberately left to Claude's mode, so the two axes never contradict.</item>
/// </list>
/// Edit tools always pass through here; their handling is Claude's edit mode, and a hook <c>allow</c> would
/// override it (hook &gt; ask in Claude's precedence). The bridge still OBSERVES every call regardless, so the
/// change feed is unaffected. A user <c>deny</c> rule still wins (deny &gt; hook).
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
