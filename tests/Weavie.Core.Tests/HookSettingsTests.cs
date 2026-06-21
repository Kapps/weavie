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
	public void BuildJson_RegistersSessionStartScopedToClear() {
		// SessionStart matches on its source, so the "clear" matcher relays only /clear (not startup/resume/
		// compact) — the event that lets the resume store drop its now-stale id.
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(HostPath));
		var group = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0];

		Assert.Equal("clear", group.GetProperty("matcher").GetString());
		string command = group.GetProperty("hooks")[0].GetProperty("command").GetString()!;
		Assert.Contains("--hook-relay", command, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildJson_QuotesHostPath() {
		using var doc = JsonDocument.Parse(HookSettings.BuildJson(HostPath));
		string command = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0]
			.GetProperty("hooks")[0].GetProperty("command").GetString()!;

		// The path is quoted on the command line so a host path with spaces survives the shell.
		Assert.StartsWith("\"" + HostPath + "\"", command, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildJson_MuxerRun_PassesEntryAssemblyBeforeRelayFlag() {
		// A framework-dependent Linux dev run: ProcessPath is the dotnet muxer, so the managed entry .dll
		// must precede --hook-relay or the muxer can't launch the relay ("dotnet --hook-relay" fails).
		const string muxer = "/usr/local/share/dotnet/dotnet";
		const string entry = "/home/user/weavie/src/Weavie.Linux/bin/Debug/net10.0/Weavie.Linux.dll";

		using var doc = JsonDocument.Parse(HookSettings.BuildJson(muxer, entry));
		string command = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0]
			.GetProperty("hooks")[0].GetProperty("command").GetString()!;

		Assert.Equal($"\"{muxer}\" \"{entry}\" --hook-relay", command);
	}
}
