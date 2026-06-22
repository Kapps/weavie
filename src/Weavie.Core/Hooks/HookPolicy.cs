namespace Weavie.Core.Hooks;

/// <summary>
/// Weavie's tool-permission gate, orthogonal to Claude's own edit mode (which Weavie only observes — see
/// <see cref="ObservedPermissionMode"/>). Driven by the <c>claude.allowAllTools</c> setting: when off, every
/// request passes through to Claude's normal flow; when on, every PermissionRequest is auto-allowed (no
/// <c>--dangerously-skip-permissions</c> needed) EXCEPT for two carve-outs that must keep passing through:
/// <list type="bullet">
/// <item>Edit tools — so Claude's own edit mode governs them; a hook <c>allow</c> would override it (hook &gt; ask).</item>
/// <item>Interactive prompts (<see cref="IsInteractivePrompt"/>) — tools that ARE a user interaction, so
/// auto-allowing them would skip the very prompt they exist for.</item>
/// </list>
/// A user <c>deny</c> rule still wins.
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
	/// Tools whose whole purpose is to prompt the user and wait on their answer: <c>ExitPlanMode</c> (plan
	/// approval) and <c>AskUserQuestion</c> (a multiple-choice question). Their PermissionRequest fires the
	/// prompt, so auto-allowing it would silently accept the plan / answer the question — defeating the
	/// interaction. Passing through instead surfaces the prompt in Claude's TUI, matching what Claude's own
	/// <c>bypassPermissions</c> does (it does NOT skip these).
	/// <para>
	/// This is a maintained name list, not a derived signal: as of Claude Code v2.x the hook payload carries no
	/// field marking a tool as interactive (no <c>tool_category</c>/<c>is_interactive</c>), so there is nothing
	/// to key off. These two are the only interactive built-in tools today — revisit when Claude Code adds more
	/// (e.g. a new built-in that waits on a user choice, or an MCP elicitation tool).
	/// </para>
	/// </summary>
	private static bool IsInteractivePrompt(string toolName) =>
		toolName is "ExitPlanMode" or "AskUserQuestion";
}
