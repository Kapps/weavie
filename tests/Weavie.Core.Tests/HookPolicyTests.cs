using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Weavie's tool-permission gate (the <c>claude.allowAllTools</c> axis): when on, it auto-allows non-edit
/// PreToolUse calls; edits (governed by Claude's own mode) and everything else pass through.
/// </summary>
public sealed class HookPolicyTests {
	private static HookRequest Pre(string tool) =>
		new() { Event = HookEventKind.PreToolUse, ToolName = tool, ToolInputJson = "{}" };

	[Fact]
	public void AllowAllOff_PassesThrough() =>
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Pre("Bash"), allowAllTools: false).Kind);

	[Theory]
	[InlineData("Bash")]
	[InlineData("WebFetch")]
	public void AllowAllOn_NonEditTool_Allows(string tool) =>
		Assert.Equal(HookDecisionKind.Allow, HookPolicy.Decide(Pre(tool), allowAllTools: true).Kind);

	[Theory]
	[InlineData("Edit")]
	[InlineData("Write")]
	[InlineData("MultiEdit")]
	[InlineData("NotebookEdit")]
	public void AllowAllOn_EditTool_PassesThrough(string tool) =>
		// Edits follow Claude's own mode, never auto-allowed here, so the two permission axes never contradict.
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Pre(tool), allowAllTools: true).Kind);

	[Fact]
	public void AllowAllOn_PostToolUse_PassesThrough() {
		// PostToolUse runs after the tool — there is nothing to allow.
		var post = new HookRequest { Event = HookEventKind.PostToolUse, ToolName = "Bash", ToolInputJson = "{}" };
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(post, allowAllTools: true).Kind);
	}
}
