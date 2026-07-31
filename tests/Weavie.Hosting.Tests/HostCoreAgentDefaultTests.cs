using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The agent default provider (<c>agent.defaultProvider</c>) is the single source of truth for the New Session
/// prompt's preselection: it's injected into the page at bootstrap, and a <c>set-agent-default</c> from the
/// prompt updates it and re-pushes the resolved value so the next prompt tracks it. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreAgentDefaultTests {
	[Fact]
	public async Task Bootstrap_InjectsTheDefaultProvider() {
		await using var host = await TestHost.StartAsync();

		Assert.Contains("window.__WEAVIE_AGENT__ = {\"defaultProvider\":\"claude\"};", host.Core.BuildBootstrap());
	}

	[Fact]
	public async Task SetAgentDefault_UpdatesAndRepushes() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.HostEvent("agentDefaults", "setProvider", new { providerId = "codex" });

		var push = host.Bridge.LastEvent("settings", "agent-defaults");
		Assert.True(push.HasValue);
		Assert.Equal("codex", push!.Value.GetProperty("defaultProvider").GetString());
	}

	[Fact]
	public async Task SetAgentDefault_CurrentProvider_DoesNotRepush() {
		await using var host = await TestHost.StartAsync(); // ships defaulting to claude
		host.Bridge.Clear();

		host.HostEvent("agentDefaults", "setProvider", new { providerId = "claude" });

		Assert.False(host.Bridge.LastEvent("settings", "agent-defaults").HasValue);
	}

	[Fact]
	public async Task SetAgentDefault_UnknownProvider_IsIgnored() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.HostEvent("agentDefaults", "setProvider", new { providerId = "ghost" });

		Assert.False(host.Bridge.LastEvent("settings", "agent-defaults").HasValue);
	}
}
