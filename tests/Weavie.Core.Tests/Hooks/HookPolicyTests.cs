using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The <c>claude.allowAllTools</c> gate: when on, auto-allows non-edit PermissionRequest events;
/// edits (governed by Claude's own mode), interactive prompts (ExitPlanMode/AskUserQuestion), and
/// observation-only PreToolUse/PostToolUse pass through.
/// </summary>
public sealed class HookPolicyTests {
	private static HookRequest Req(string tool) =>
		new() { Event = HookEventKind.PermissionRequest, ToolName = tool, ToolInputJson = "{}" };

	[Fact]
	public void AllowAllOff_PassesThrough() =>
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Req("Bash"), allowAllTools: false).Kind);

	[Theory]
	[InlineData("Bash")]
	[InlineData("WebFetch")]
	public void AllowAllOn_NonEditTool_Allows(string tool) =>
		Assert.Equal(HookDecisionKind.Allow, HookPolicy.Decide(Req(tool), allowAllTools: true).Kind);

	[Theory]
	[InlineData("Edit")]
	[InlineData("Write")]
	[InlineData("MultiEdit")]
	[InlineData("NotebookEdit")]
	public void AllowAllOn_EditTool_PassesThrough(string tool) =>
		// Edits follow Claude's own mode, never auto-allowed here.
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Req(tool), allowAllTools: true).Kind);

	[Theory]
	[InlineData("ExitPlanMode")]
	[InlineData("AskUserQuestion")]
	public void AllowAllOn_InteractivePrompt_PassesThrough(string tool) =>
		// These tools ARE the user prompt — auto-allowing them would silently accept the plan / answer the
		// question. They pass through so the prompt surfaces, matching Claude's own bypassPermissions.
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Req(tool), allowAllTools: true).Kind);

	[Fact]
	public void AllowAllOn_PreToolUse_PassesThrough() {
		// PreToolUse is observation-only (change tracking) and never decides.
		var pre = new HookRequest { Event = HookEventKind.PreToolUse, ToolName = "Bash", ToolInputJson = "{}" };
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(pre, allowAllTools: true).Kind);
	}

	[Fact]
	public void AllowAllOn_PostToolUse_PassesThrough() {
		// PostToolUse runs after the tool — there is nothing to allow.
		var post = new HookRequest { Event = HookEventKind.PostToolUse, ToolName = "Bash", ToolInputJson = "{}" };
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(post, allowAllTools: true).Kind);
	}
}
