namespace Weavie.Core.Hooks;

/// <summary>
/// Weavie's tool-permission gate, driven by <c>claude.allowAllTools</c>: off ⇒ everything passes through to
/// Claude's flow; on ⇒ every PermissionRequest is auto-allowed EXCEPT two carve-outs that must keep passing
/// through (a user <c>deny</c> rule still wins):
/// <list type="bullet">
/// <item>Edit tools — so Claude's own edit mode governs them; a hook <c>allow</c> would override it (hook &gt; ask).</item>
/// <item>Interactive prompts (<see cref="IsInteractivePrompt"/>) — auto-allowing them would skip the very prompt they exist for.</item>
/// </list>
/// </summary>
public static class HookPolicy {
	/// <summary>Returns the bridge's verdict for <paramref name="request"/> given the <c>claude.allowAllTools</c> setting.</summary>
	/// <param name="request">The observed hook event.</param>
	/// <param name="allowAllTools">Whether Weavie auto-allows tool calls (the <c>claude.allowAllTools</c> setting).</param>
	public static HookDecision Decide(HookRequest request, bool allowAllTools) {
		ArgumentNullException.ThrowIfNull(request);
		if (allowAllTools
			&& request.Event == HookEventKind.PermissionRequest
			&& !IsEditTool(request.ToolName)
			&& !IsInteractivePrompt(request.ToolName)) {
			return HookDecision.Allow("Weavie allow-all-tools");
		}

		return HookDecision.PassThrough;
	}

	/// <summary>The mutating edit tools, whose permission is governed by Claude's edit mode — never auto-allowed here.</summary>
	private static bool IsEditTool(string toolName) =>
		toolName is "Edit" or "Write" or "MultiEdit" or "NotebookEdit";

	/// <summary>
	/// Tools that ARE a user interaction — <c>ExitPlanMode</c> and <c>AskUserQuestion</c> — so auto-allowing
	/// their PermissionRequest would silently accept the plan / answer the question. A maintained name list, not
	/// a derived signal: the hook payload carries no interactivity field, so revisit when Claude Code adds more.
	/// </summary>
	private static bool IsInteractivePrompt(string toolName) =>
		toolName is "ExitPlanMode" or "AskUserQuestion";
}
