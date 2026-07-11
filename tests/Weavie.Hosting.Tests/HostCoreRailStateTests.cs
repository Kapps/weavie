using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The rail-state push resolves the remembered new-session provider against the live provider registry: a
/// registered id rides through unchanged, a stale one drops back to the configured <c>agent.defaultProvider</c>.
/// The persistence store keeps the id raw; this boundary owns validation. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreRailStateTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	private static string? PushedProvider(TestHost host) =>
		host.Bridge.LastOfType("rail-state")?.GetProperty("lastAgentProvider").GetString();

	[Fact]
	public async Task RailState_PushesRememberedRegisteredProvider() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "set-last-agent-provider", providerId = "codex" }));

		Assert.Equal("codex", PushedProvider(host));
	}

	[Fact]
	public async Task RailState_DropsStaleRememberedProvider_BackToConfiguredDefault() {
		await using var host = await TestHost.StartAsync();
		string railFile = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "rail-state.json");

		await host.RestartAsync(() => File.WriteAllText(railFile,
			"""{"version":1,"lastAgentProvider":"ghost","promoted":[]}"""));

		Assert.Equal("claude", PushedProvider(host));
	}
}
