using System.Text.Json;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreAgentPlanTests {
	[Fact]
	public async Task OpenAgentPlan_RoutesTheExactCompletedPlanThroughItsSessionsEditorChannel() {
		await using var host = await TestHost.StartAsync();
		var created = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = "agent-plan",
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(created.Ok, created.Error);
		host.MountEditorProjection();
		host.Send(JsonSerializer.Serialize(new {
			type = "agent-submit",
			slot = "agent-plan",
			prompt = FakeCodexAgentProvider.PlanPrompt,
		}));
		host.Bridge.Clear();
		host.Send("""{"type":"open-agent-plan","threadId":"thread-fake","turnId":"turn-1","itemId":"plan-1"}""");
		Assert.Empty(host.Bridge.PostedOfType("show-agent-plan"));

		host.Send("""{"type":"open-agent-plan","slot":"agent-plan","threadId":"thread-fake","turnId":"turn-1","itemId":"plan-1"}""");

		var plan = Assert.Single(host.Bridge.PostedOfType("show-agent-plan"));
		Assert.StartsWith(host.Core.ActiveSessionForTest()!.Id + ":", plan.GetProperty("id").GetString());
		Assert.Equal("Plan", plan.GetProperty("title").GetString());
		Assert.Equal(FakeCodexAgentProvider.PlanMarkdown, plan.GetProperty("markdown").GetString());
	}

	[Fact]
	public async Task OpenAgentPlan_RejectsAResetPlansIdentity() {
		await using var host = await TestHost.StartAsync();
		var created = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = "stale-agent-plan",
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(created.Ok, created.Error);
		host.MountEditorProjection();
		host.Send(JsonSerializer.Serialize(new {
			type = "agent-submit",
			slot = "stale-agent-plan",
			prompt = FakeCodexAgentProvider.PlanPrompt,
		}));
		host.Send(JsonSerializer.Serialize(new {
			type = "agent-submit",
			slot = "stale-agent-plan",
			prompt = FakeCodexAgentProvider.ResetPrompt,
		}));
		host.Bridge.Clear();

		host.Send("""{"type":"open-agent-plan","slot":"stale-agent-plan","threadId":"thread-fake","turnId":"turn-1","itemId":"plan-1"}""");

		Assert.Empty(host.Bridge.PostedOfType("show-agent-plan"));
		Assert.Equal("That plan is no longer available.", host.Bridge.LastOfType("notify")?.GetProperty("message").GetString());
	}

	[Fact]
	public async Task OpenAgentPlan_ForABackgroundSlotWaitsForItsEditorProjection() {
		await using var host = await TestHost.StartAsync();
		string primarySlot = host.PrimaryId;
		var created = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = "background-agent-plan",
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(created.Ok, created.Error);
		host.MountEditorProjection();
		host.Send(JsonSerializer.Serialize(new {
			type = "agent-submit",
			slot = "background-agent-plan",
			prompt = FakeCodexAgentProvider.PlanPrompt,
		}));
		host.Send(JsonSerializer.Serialize(new { type = "switch-session", id = primarySlot }));
		host.Bridge.Clear();

		host.Send("""{"type":"open-agent-plan","slot":"background-agent-plan","threadId":"thread-fake","turnId":"turn-1","itemId":"plan-1"}""");

		Assert.Empty(host.Bridge.PostedOfType("show-agent-plan"));
		host.Send(JsonSerializer.Serialize(new { type = "switch-session", id = "background-agent-plan" }));
		Assert.Single(host.Bridge.PostedOfType("show-agent-plan"));
	}
}
