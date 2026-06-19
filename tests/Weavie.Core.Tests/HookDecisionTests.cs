using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The decision → <c>hookSpecificOutput</c> stdout JSON Claude reads (pass-through = nothing).</summary>
public sealed class HookDecisionTests {
	[Fact]
	public void PassThrough_SerializesToNull() =>
		Assert.Null(HookDecision.PassThrough.ToHookOutputJson(HookEventKind.PreToolUse));

	[Fact]
	public void Allow_PreToolUse_EmitsAllowDecision() {
		string? json = HookDecision.Allow("bypass mode").ToHookOutputJson(HookEventKind.PreToolUse);

		Assert.NotNull(json);
		Assert.Contains("\"permissionDecision\":\"allow\"", json, StringComparison.Ordinal);
		Assert.Contains("\"hookEventName\":\"PreToolUse\"", json, StringComparison.Ordinal);
		Assert.Contains("bypass mode", json, StringComparison.Ordinal);
	}

	[Fact]
	public void Deny_PreToolUse_EmitsDenyDecision() {
		string? json = HookDecision.Deny("nope").ToHookOutputJson(HookEventKind.PreToolUse);

		Assert.NotNull(json);
		Assert.Contains("\"permissionDecision\":\"deny\"", json, StringComparison.Ordinal);
	}

	[Fact]
	// Only PreToolUse carries a permission decision; PostToolUse runs after the tool.
	public void Allow_NonPreToolUseEvent_SerializesToNull() =>
		Assert.Null(HookDecision.Allow("x").ToHookOutputJson(HookEventKind.PostToolUse));

	[Fact]
	public void SystemMessage_PostToolUse_EmitsTopLevelSystemMessage() {
		var decision = HookDecision.PassThrough with { SystemMessage = "src/foo.ts:42" };

		string? json = decision.ToHookOutputJson(HookEventKind.PostToolUse);

		Assert.NotNull(json);
		Assert.Contains("\"systemMessage\":\"src/foo.ts:42\"", json, StringComparison.Ordinal);
		// A PostToolUse message carries no permission block.
		Assert.DoesNotContain("hookSpecificOutput", json, StringComparison.Ordinal);
	}

	[Fact]
	// A pass-through with no message stays silent (Claude's normal flow).
	public void PassThrough_NoMessage_SerializesToNull() =>
		Assert.Null((HookDecision.PassThrough with { SystemMessage = null }).ToHookOutputJson(HookEventKind.PostToolUse));

	[Fact]
	public void Allow_WithSystemMessage_EmitsBoth() {
		var decision = HookDecision.Allow("bypass mode") with { SystemMessage = "a.cs:1" };

		string? json = decision.ToHookOutputJson(HookEventKind.PreToolUse);

		Assert.NotNull(json);
		Assert.Contains("\"systemMessage\":\"a.cs:1\"", json, StringComparison.Ordinal);
		Assert.Contains("\"permissionDecision\":\"allow\"", json, StringComparison.Ordinal);
	}
}
