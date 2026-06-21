using System.Text.Json;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The <c>--settings</c> JSON: a hooks block routing PermissionRequest (the permission gate, every tool) and the edit tools' PreToolUse/PostToolUse (change tracking) to the standalone relay binary.</summary>
public sealed class HookSettingsTests {
	private const string RelayPath = @"C:\app\weavie-hook-relay.exe";

	[Fact]
	public void BuildJson_PermissionRequestGatesAllTools_PreAndPostObserveEditsOnly() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(RelayPath));
		var hooks = doc.RootElement.GetProperty("hooks");

		// The permission gate matches EVERY tool so claude.allowAllTools can bypass any tool that would prompt;
		// it fires only on a real prompt, so auto-allowed tools cost nothing.
		Assert.Equal("*", HookSettings.PermissionMatcher);
		Assert.Equal(HookSettings.PermissionMatcher, hooks.GetProperty("PermissionRequest")[0].GetProperty("matcher").GetString());

		// Pre/PostToolUse are observation-only (change tracking), scoped to the edit tools.
		Assert.DoesNotContain("Bash", HookSettings.ObserveMatcher, StringComparison.Ordinal);
		foreach (string eventName in new[] { "PreToolUse", "PostToolUse" }) {
			Assert.Equal(HookSettings.ObserveMatcher, hooks.GetProperty(eventName)[0].GetProperty("matcher").GetString());
		}
	}

	[Fact]
	public void BuildJson_EveryHookRunsTheStandaloneRelayDirectly() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(RelayPath));
		var hooks = doc.RootElement.GetProperty("hooks");

		// Every hook runs the standalone relay binary directly — no host, no --hook-relay flag, no fork.
		foreach (string eventName in new[] { "PreToolUse", "PostToolUse", "PermissionRequest", "UserPromptSubmit", "Stop", "Notification", "SessionStart" }) {
			string command = hooks.GetProperty(eventName)[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
			Assert.Equal("\"" + RelayPath + "\"", command);
			Assert.DoesNotContain("--hook-relay", command, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void BuildJson_RegistersSessionStartScopedToClear() {
		// SessionStart matches on its source, so the "clear" matcher relays only /clear (not startup/resume/
		// compact) — the event that lets the resume store drop its now-stale id.
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(RelayPath));
		var group = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0];

		Assert.Equal("clear", group.GetProperty("matcher").GetString());
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
