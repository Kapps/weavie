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
}
