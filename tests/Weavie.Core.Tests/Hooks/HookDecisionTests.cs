using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The decision serializes to the stdout JSON Claude reads (pass-through = nothing).</summary>
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
	public void Allow_PermissionRequest_EmitsNestedDecisionBehavior() {
		string? json = HookDecision.Allow("bypass mode").ToHookOutputJson(HookEventKind.PermissionRequest);

		Assert.NotNull(json);
		Assert.Contains("\"hookEventName\":\"PermissionRequest\"", json, StringComparison.Ordinal);
		// PermissionRequest nests the verdict ({decision:{behavior}}), unlike PreToolUse's flat permissionDecision.
		Assert.Contains("\"decision\":{\"behavior\":\"allow\"}", json, StringComparison.Ordinal);
		Assert.DoesNotContain("permissionDecision", json, StringComparison.Ordinal);
	}

	[Fact]
	public void Deny_PermissionRequest_EmitsNestedDenyBehavior() {
		string? json = HookDecision.Deny("nope").ToHookOutputJson(HookEventKind.PermissionRequest);

		Assert.NotNull(json);
		Assert.Contains("\"behavior\":\"deny\"", json, StringComparison.Ordinal);
	}

	[Fact]
	// PostToolUse runs after the tool, so it carries no permission decision.
	public void Allow_PostToolUse_SerializesToNull() =>
		Assert.Null(HookDecision.Allow("x").ToHookOutputJson(HookEventKind.PostToolUse));

	[Fact]
	public void SystemMessage_PostToolUse_EmitsTopLevelSystemMessage() {
		var decision = HookDecision.PassThrough with { SystemMessage = "src/foo.ts:42" };

		string? json = decision.ToHookOutputJson(HookEventKind.PostToolUse);

		Assert.NotNull(json);
		Assert.Contains("\"systemMessage\":\"src/foo.ts:42\"", json, StringComparison.Ordinal);
		// PostToolUse carries no permission block.
		Assert.DoesNotContain("hookSpecificOutput", json, StringComparison.Ordinal);
	}

	[Fact]
	// Pass-through with no message stays silent.
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
