using System.Text.Json;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The <c>--settings</c> JSON routes PermissionRequest (gate, every tool) and the edit tools' PreToolUse/PostToolUse (change tracking) to the relay binary.</summary>
public sealed class HookSettingsTests {
	private const string RelayPath = @"C:\app\weavie-hook-relay.exe";

	[Fact]
	public void BuildJson_PermissionRequestGatesAllTools_PreAndPostObserveEditsOnly() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(RelayPath));
		var hooks = doc.RootElement.GetProperty("hooks");

		// The gate matches every tool so claude.allowAllTools can bypass anything that would prompt.
		Assert.Equal("*", HookSettings.PermissionMatcher);
		Assert.Equal(HookSettings.PermissionMatcher, hooks.GetProperty("PermissionRequest")[0].GetProperty("matcher").GetString());

		// Pre/PostToolUse are observation-only, scoped to the edit tools.
		Assert.DoesNotContain("Bash", HookSettings.ObserveMatcher, StringComparison.Ordinal);
		foreach (string eventName in new[] { "PreToolUse", "PostToolUse" }) {
			Assert.Equal(HookSettings.ObserveMatcher, hooks.GetProperty(eventName)[0].GetProperty("matcher").GetString());
		}
	}

	[Fact]
	public void BuildJson_EveryHookRunsTheStandaloneRelayDirectly() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(RelayPath));
		var hooks = doc.RootElement.GetProperty("hooks");

		// Every hook runs the relay binary directly, with no --hook-relay flag.
		foreach (string eventName in new[] { "PreToolUse", "PostToolUse", "PermissionRequest", "UserPromptSubmit", "Stop", "Notification", "SessionStart" }) {
			string command = hooks.GetProperty(eventName)[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
			Assert.Equal("\"" + RelayPath + "\"", command);
			Assert.DoesNotContain("--hook-relay", command, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void BuildJson_RegistersSessionStartForAllSources() {
		// No matcher: SessionStart relays on every source (startup/resume/clear/compact), so the first one
		// clears the session status out of Starting; the source-specific handlers filter for themselves.
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(RelayPath));
		var group = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0];

		Assert.False(group.TryGetProperty("matcher", out _));
	}

	[Fact]
	public void BuildJson_QuotesRelayPath() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(@"C:\Program Files\Weavie\weavie-hook-relay.exe"));
		string command = doc.RootElement.GetProperty("hooks").GetProperty("PermissionRequest")[0]
			.GetProperty("hooks")[0].GetProperty("command").GetString()!;

		// Quoted so a relay path with spaces survives the shell.
		Assert.Equal("\"C:\\Program Files\\Weavie\\weavie-hook-relay.exe\"", command);
	}
}
