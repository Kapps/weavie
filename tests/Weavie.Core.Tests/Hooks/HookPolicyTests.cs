using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Weavie's tool-permission gate (the <c>claude.allowAllTools</c> axis): when on, it auto-allows non-edit
/// PermissionRequest events (the dialog-time hook); edits (governed by Claude's own mode) and the
/// observation-only PreToolUse/PostToolUse events pass through.
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
		// Edits follow Claude's own mode, never auto-allowed here, so the two permission axes never contradict.
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Req(tool), allowAllTools: true).Kind);

	[Fact]
	public void AllowAllOn_PreToolUse_PassesThrough() {
		// The gate is PermissionRequest now; PreToolUse is observation-only (change tracking) and never decides.
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
