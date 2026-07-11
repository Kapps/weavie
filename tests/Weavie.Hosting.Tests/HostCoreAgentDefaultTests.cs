using Weavie.Core.Commands;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The agent default provider (<c>agent.defaultProvider</c>) is the single source of truth for the New Session
/// prompt's preselection: it's injected into the page at bootstrap, and creating a session with a different
/// provider updates it and re-pushes the resolved value so the next prompt tracks it. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreAgentDefaultTests {
	private static Task<CommandResult> CreateWith(TestHost host, string branch, string provider) =>
		host.Core.NewSessionAsync(
			new NewSessionRequest { Branch = branch, Base = "main", AttachExisting = false, AgentProviderId = provider },
			CancellationToken.None);

	[Fact]
	public async Task Bootstrap_InjectsTheDefaultProvider() {
		await using var host = await TestHost.StartAsync();

		Assert.Contains("window.__WEAVIE_AGENT__ = {\"defaultProvider\":\"claude\"};", host.Core.BuildBootstrap());
	}

	[Fact]
	public async Task CreatingSessionWithProvider_BecomesTheDefault_AndRepushes() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		await CreateWith(host, "feature/codex-default", "codex");

		var push = host.Bridge.LastOfType("agent-defaults");
		Assert.True(push.HasValue);
		Assert.Equal("codex", push!.Value.GetProperty("defaultProvider").GetString());
	}

	[Fact]
	public async Task CreatingSessionWithCurrentDefault_DoesNotRepush() {
		await using var host = await TestHost.StartAsync(); // ships defaulting to claude
		host.Bridge.Clear();

		await CreateWith(host, "feature/keep-claude", "claude");

		Assert.False(host.Bridge.LastOfType("agent-defaults").HasValue);
	}
}
