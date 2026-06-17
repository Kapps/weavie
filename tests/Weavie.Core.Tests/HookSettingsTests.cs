using System.Text.Json;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The <c>--settings</c> JSON: a hooks block routing the mutating tools to the relay for both events.</summary>
public sealed class HookSettingsTests {
	private const string HostPath = @"C:\Program Files\Weavie\Weavie.Win.exe";

	[Fact]
	public void BuildJson_RoutesMutatingToolsToRelayForBothEvents() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(HostPath));
		var hooks = doc.RootElement.GetProperty("hooks");

		foreach (string eventName in new[] { "PreToolUse", "PostToolUse" }) {
			var group = hooks.GetProperty(eventName)[0];
			Assert.Equal(HookSettings.ToolMatcher, group.GetProperty("matcher").GetString());

			var hook = group.GetProperty("hooks")[0];
			Assert.Equal("command", hook.GetProperty("type").GetString());
			string command = hook.GetProperty("command").GetString()!;
			Assert.Contains("--hook-relay", command, StringComparison.Ordinal);
			Assert.Contains("Weavie.Win.exe", command, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void BuildJson_QuotesHostPath() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(HostPath));
		string command = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0]
			.GetProperty("hooks")[0].GetProperty("command").GetString()!;

		// The path is quoted on the command line so a host path with spaces survives the shell.
		Assert.StartsWith("\"" + HostPath + "\"", command, StringComparison.Ordinal);
	}
}
