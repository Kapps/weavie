using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreStructuredAgentLifecycleTests {
	[Fact]
	public async Task NewStructuredSession_StartsWhenItsOwnedEndpointActivates() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));

		var created = await host.InvokeCommandAsync(
			"primary",
			SessionCommands.NewSession,
			new NewSessionRequest {
				Branch = "structured-lifecycle",
				Base = "main",
				AgentProviderId = "codex",
			},
			CancellationToken.None);

		Assert.True(created.Ok, created.Error);
		Assert.Equal("primary", host.SelectedSession.SlotId);
		var session = host.Session("structured-lifecycle");
		await session.Agent.DrainPaneAsync(CancellationToken.None);
		var started = Assert.Single(
			host.Bridge.PostedEvents(session.Address, "agent", "pane"),
			message => message.GetProperty("type").GetString() == "thread-ready");
		Assert.Equal("ready", started.GetProperty("status").GetString());

		host.SessionEvent(
			session,
			"agent",
			"submit",
			new {
				id = "first-turn",
				prompt = "hello",
				attachmentIds = Array.Empty<string>(),
				skills = Array.Empty<string>(),
			});

		Assert.Contains(
			host.Bridge.PostedEvents(session.Address, "agent", "pane"),
			message => message.GetProperty("type").GetString() == "item-completed"
				&& message.GetProperty("text").GetString() == "echo: hello");
	}
}
