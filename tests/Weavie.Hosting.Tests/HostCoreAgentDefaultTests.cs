using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
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

		string bootstrap = host.Core.BuildBootstrap();
		Assert.Contains("window.__WEAVIE_AGENT__ = {\"defaultProvider\":\"claude\",\"providers\":[", bootstrap);
		Assert.Contains("\"id\":\"claude\",\"name\":\"Claude Code\"", bootstrap);
	}

	[Fact]
	public async Task ReplyStartOffset_IsInjectedAndRepushedOnChange() {
		await using var host = await TestHost.StartAsync();
		Assert.Contains("\"replyStartOffset\":0.5,\"thoughtDisplay\":\"collapsed\"}", host.Core.BuildBootstrap());
		host.Bridge.Clear();

		host.Settings.Set(AgentSettings.ReplyStartOffset, JsonSerializer.SerializeToElement(0.25));

		var push = host.Bridge.LastEvent("settings", "agent-defaults");
		Assert.Equal(0.25, push!.Value.GetProperty("replyStartOffset").GetDouble());
	}

	[Fact]
	public async Task ThoughtDisplay_IsCollapsedByDefaultAndRepushesTheValidatedChoice() {
		await using var host = await TestHost.StartAsync();
		var defaults = await host.HostRequestAsync<JsonElement>("agentDefaults", "get", new { });
		Assert.Equal("collapsed", defaults.GetProperty("thoughtDisplay").GetString());
		host.Bridge.Clear();
		host.Settings.Set(AgentSettings.ThoughtDisplay, JsonSerializer.SerializeToElement("inline"));
		var push = host.Bridge.LastEvent("settings", "agent-defaults");
		Assert.Equal("inline", push!.Value.GetProperty("thoughtDisplay").GetString());
		Assert.Throws<SettingValidationException>(() => host.Settings.Set(
			AgentSettings.ThoughtDisplay, JsonSerializer.SerializeToElement("guessed-replies")));
	}

	[Fact]
	public async Task SetAgentDefault_UpdatesAndRepushes() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		var response = await host.HostRequestAsync<JsonElement>(
			"agentDefaults", "setProvider", new { providerId = "structured" });

		var push = host.Bridge.LastEvent("settings", "agent-defaults");
		Assert.True(push.HasValue);
		Assert.Equal("structured", push!.Value.GetProperty("defaultProvider").GetString());
		Assert.Equal("structured", response.GetProperty("defaultProvider").GetString());
	}

	[Fact]
	public async Task SetAgentDefault_CurrentProvider_DoesNotRepush() {
		await using var host = await TestHost.StartAsync(); // ships defaulting to claude
		host.Bridge.Clear();

		var response = await host.HostRequestAsync<JsonElement>(
			"agentDefaults", "setProvider", new { providerId = "claude" });

		Assert.False(host.Bridge.LastEvent("settings", "agent-defaults").HasValue);
		Assert.Equal("claude", response.GetProperty("defaultProvider").GetString());
	}

	[Fact]
	public async Task SetAgentDefault_UnknownProvider_IsIgnored() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		var response = await host.HostRequestAsync<JsonElement>(
			"agentDefaults", "setProvider", new { providerId = "ghost" });

		Assert.False(host.Bridge.LastEvent("settings", "agent-defaults").HasValue);
		Assert.Equal("claude", response.GetProperty("defaultProvider").GetString());
	}

	[Fact]
	public async Task CreatingSession_RemembersItsProvider() {
		await using var host = await TestHost.StartAsync();

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "remember-structured",
			Base = "main",
			AgentProviderId = "structured",
		});

		Assert.True(result.Ok);
		Assert.Contains("window.__WEAVIE_AGENT__ = {\"defaultProvider\":\"structured\"", host.Core.BuildBootstrap());
	}
}
