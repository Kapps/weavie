using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The agent default provider (<c>agent.defaultProvider</c>) is the single source of truth for the New Session
/// prompt's preselection: it's injected into the page at bootstrap, and a <c>set-agent-default</c> from the
/// prompt updates it and re-pushes the resolved value so the next prompt tracks it. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreAgentDefaultTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	[Fact]
	public async Task Bootstrap_InjectsTheDefaultProvider() {
		await using var host = await TestHost.StartAsync();

		Assert.Contains("window.__WEAVIE_AGENT__ = {\"defaultProvider\":\"claude\"};", host.Core.BuildBootstrap());
	}

	[Fact]
	public async Task SetAgentDefault_UpdatesAndRepushes() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.Send(Msg(new { type = "set-agent-default", providerId = "codex" }));

		var push = host.Bridge.LastOfType("agent-defaults");
		Assert.True(push.HasValue);
		Assert.Equal("codex", push!.Value.GetProperty("defaultProvider").GetString());
	}

	[Fact]
	public async Task SetAgentDefault_CurrentProvider_DoesNotRepush() {
		await using var host = await TestHost.StartAsync(); // ships defaulting to claude
		host.Bridge.Clear();

		host.Send(Msg(new { type = "set-agent-default", providerId = "claude" }));

		Assert.False(host.Bridge.LastOfType("agent-defaults").HasValue);
	}

	[Fact]
	public async Task SetAgentDefault_UnknownProvider_IsIgnored() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.Send(Msg(new { type = "set-agent-default", providerId = "ghost" }));

		Assert.False(host.Bridge.LastOfType("agent-defaults").HasValue);
	}
}
