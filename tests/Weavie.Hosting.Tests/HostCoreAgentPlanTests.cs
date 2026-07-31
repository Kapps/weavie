using Weavie.Core.Sessions;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreAgentPlanTests {
	private static void Submit(TestHost host, HostSession session, string prompt) =>
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt, attachmentIds = Array.Empty<string>(), skills = Array.Empty<string>() });

	[Fact]
	public async Task OpenAgentPlan_RoutesTheExactCompletedPlanThroughItsSessionsEditorChannel() {
		await using var host = await TestHost.StartAsync();
		var created = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "agent-plan",
			Base = "main",
			AgentProviderId = "codex",
		});
		Assert.True(created.Ok, created.Error);
		var session = host.Session("agent-plan");
		Submit(host, session, FakeCodexAgentProvider.PlanPrompt);
		host.Bridge.Clear();

		bool wrongSession = await host.SessionRequestAsync<bool>(
			host.PrimarySession,
			"agent",
			"openPlan",
			new { threadId = "thread-fake", turnId = "turn-1", itemId = "plan-1" });
		bool opened = await host.SessionRequestAsync<bool>(
			session,
			"agent",
			"openPlan",
			new { threadId = "thread-fake", turnId = "turn-1", itemId = "plan-1" });

		Assert.False(wrongSession);
		Assert.True(opened);
		Assert.Empty(host.Bridge.PostedEvents(host.PrimarySession.Address, "editor", "agentPlan"));
		var plan = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "agentPlan"));
		Assert.Equal(
			AgentPaneIdentity.ItemKey("thread-fake", "turn-1", "plan-1"),
			plan.GetProperty("id").GetString());
		Assert.Equal("Plan", plan.GetProperty("title").GetString());
		Assert.Equal(FakeCodexAgentProvider.PlanMarkdown, plan.GetProperty("markdown").GetString());
	}

	[Fact]
	public async Task OpenAgentPlan_RejectsAResetPlansIdentity() {
		await using var host = await TestHost.StartAsync();
		var created = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "stale-agent-plan",
			Base = "main",
			AgentProviderId = "codex",
		});
		Assert.True(created.Ok, created.Error);
		var session = host.Session("stale-agent-plan");
		Submit(host, session, FakeCodexAgentProvider.PlanPrompt);
		Submit(host, session, FakeCodexAgentProvider.ResetPrompt);
		host.Bridge.Clear();

		bool opened = await host.SessionRequestAsync<bool>(
			session,
			"agent",
			"openPlan",
			new { threadId = "thread-fake", turnId = "turn-1", itemId = "plan-1" });

		Assert.False(opened);
		Assert.Empty(host.Bridge.PostedEvents(session.Address, "editor", "agentPlan"));
	}

	[Fact]
	public async Task OpenAgentPlan_ForABackgroundSessionPublishesImmediatelyToThatSession() {
		await using var host = await TestHost.StartAsync();
		var created = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "background-agent-plan",
			Base = "main",
			AgentProviderId = "codex",
		});
		Assert.True(created.Ok, created.Error);
		var background = host.Session("background-agent-plan");
		Submit(host, background, FakeCodexAgentProvider.PlanPrompt);
		host.SelectSession("primary");
		host.Bridge.Clear();

		bool opened = await host.SessionRequestAsync<bool>(
			background,
			"agent",
			"openPlan",
			new { threadId = "thread-fake", turnId = "turn-1", itemId = "plan-1" });

		Assert.True(opened);
		Assert.Equal("primary", host.SelectedSession.SlotId);
		Assert.Single(host.Bridge.PostedEvents(background.Address, "editor", "agentPlan"));
		Assert.Empty(host.Bridge.PostedEvents(host.PrimarySession.Address, "editor", "agentPlan"));
	}
}
