using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The active-mode gate: bypass auto-allows PreToolUse; default/acceptEdits pass through.</summary>
public sealed class HookPolicyTests {
	private static HookRequest Pre(string tool = "Bash") =>
		new() { Event = HookEventKind.PreToolUse, ToolName = tool, ToolInputJson = "{}" };

	[Theory]
	[InlineData("default")]
	[InlineData("acceptEdits")]
	public void Decide_NonBypassModes_PassThrough(string mode) =>
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(Pre(), mode).Kind);

	[Fact]
	public void Decide_Bypass_PreToolUse_Allows() =>
		Assert.Equal(HookDecisionKind.Allow, HookPolicy.Decide(Pre(), "bypassPermissions").Kind);

	[Fact]
	public void Decide_Bypass_PostToolUse_PassesThrough() {
		// PostToolUse runs after the tool — there is nothing to allow.
		var post = new HookRequest { Event = HookEventKind.PostToolUse, ToolName = "Bash", ToolInputJson = "{}" };
		Assert.Equal(HookDecisionKind.PassThrough, HookPolicy.Decide(post, "bypassPermissions").Kind);
	}
}
