using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreAgentPlanTests {
	[Fact]
	public async Task OpenPlan_ReceivesFullRevisionsAndReplaysWithoutReopeningOrSelectingItsSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "live-plan",
			Base = "main",
			AgentProviderId = "structured",
		})).Ok);
		var session = host.Session("live-plan");
		Submit(host, session, FakeStructuredAgentProvider.PlanPrompt);
		Assert.True(session.OpenAgentPlan("thread-fake", "turn-1", "plan-1"));
		string? path = session.EditorSession.Active;
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		Submit(host, session, FakeStructuredAgentProvider.PlanRevisionPrompt);
		Submit(host, session, FakeStructuredAgentProvider.PlanReplayPrompt);
		var revisions = host.Bridge.PostedEvents(session.Address, "editor", "agentPlan").ToArray();
		Assert.Equal(2, revisions.Length);
		Assert.All(revisions, plan => {
			Assert.Equal(path, plan.GetProperty("path").GetString());
			Assert.Equal(FakeStructuredAgentProvider.RevisedPlanMarkdown, plan.GetProperty("markdown").GetString());
		});
		Assert.Empty(host.Bridge.PostedEvents(session.Address, "editor", "agentPlanRemoved"));
		Assert.Empty(host.Bridge.PostedEvents(session.Address, "editor", "openOverlay"));
		Assert.Same(host.WorkspaceSession, host.SelectedSession);
		Assert.Equal(path, session.EditorSession.Active);

		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(session, "lifecycle", "sync", new { });
		var replay = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "agentPlan"));
		Assert.Equal(FakeStructuredAgentProvider.RevisedPlanMarkdown, replay.GetProperty("markdown").GetString());
	}

	[Theory]
	[InlineData(FakeStructuredAgentProvider.PlanRemovalPrompt)]
	[InlineData(FakeStructuredAgentProvider.ResetPrompt)]
	[InlineData(FakeStructuredAgentProvider.PlanRemovedReplayPrompt)]
	public async Task RemovedPlan_InvalidatesItsOpenDocumentAndCannotReplayStaleContent(string prompt) {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "removed-plan",
			Base = "main",
			AgentProviderId = "structured",
		})).Ok);
		var session = host.Session("removed-plan");
		Submit(host, session, FakeStructuredAgentProvider.PlanPrompt);
		Assert.True(session.OpenAgentPlan("thread-fake", "turn-1", "plan-1"));
		string? path = session.EditorSession.Active;
		host.Bridge.Clear();
		Submit(host, session, prompt);
		var removed = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "agentPlanRemoved"));
		Assert.Equal(path, removed.GetProperty("path").GetString());
		Assert.Equal(path, Assert.Single(session.EditorSession.Open).Path);
		Assert.False(session.OpenAgentPlan("thread-fake", "turn-1", "plan-1"));

		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(session, "lifecycle", "sync", new { });
		Assert.Empty(host.Bridge.PostedEvents(session.Address, "editor", "agentPlan"));
		Assert.Equal(path, Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "agentPlanRemoved"))
			.GetProperty("path").GetString());
	}

	private static void Submit(TestHost host, HostSession session, string prompt) =>
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt, kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });

	[Fact]
	public async Task OpenAgentPlan_RoutesTheExactCompletedPlanThroughItsSessionsEditorChannel() {
		await using var host = await TestHost.StartAsync();
		var created = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "agent-plan",
			Base = "main",
			AgentProviderId = "structured",
		});
		Assert.True(created.Ok, created.Error);
		var session = host.Session("agent-plan");
		Submit(host, session, FakeStructuredAgentProvider.PlanPrompt);
		host.Bridge.Clear();

		bool wrongSession = await host.SessionRequestAsync<bool>(
			host.WorkspaceSession,
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
		Assert.Empty(host.Bridge.PostedEvents(host.WorkspaceSession.Address, "editor", "agentPlan"));
		var plan = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "agentPlan"));
		Assert.Equal(
			AgentPaneIdentity.ItemKey("thread-fake", "turn-1", "plan-1"),
			plan.GetProperty("id").GetString());
		Assert.Equal("Plan", plan.GetProperty("title").GetString());
		Assert.Equal(FakeStructuredAgentProvider.PlanMarkdown, plan.GetProperty("markdown").GetString());
	}

	[Fact]
	public async Task OpenAgentPlan_RejectsAResetPlansIdentity() {
		await using var host = await TestHost.StartAsync();
		var created = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "stale-agent-plan",
			Base = "main",
			AgentProviderId = "structured",
		});
		Assert.True(created.Ok, created.Error);
		var session = host.Session("stale-agent-plan");
		Submit(host, session, FakeStructuredAgentProvider.PlanPrompt);
		Submit(host, session, FakeStructuredAgentProvider.ResetPrompt);
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
			AgentProviderId = "structured",
		});
		Assert.True(created.Ok, created.Error);
		var background = host.Session("background-agent-plan");
		Submit(host, background, FakeStructuredAgentProvider.PlanPrompt);
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		bool opened = await host.SessionRequestAsync<bool>(
			background,
			"agent",
			"openPlan",
			new { threadId = "thread-fake", turnId = "turn-1", itemId = "plan-1" });

		Assert.True(opened);
		Assert.Same(host.WorkspaceSession, host.SelectedSession);
		Assert.Single(host.Bridge.PostedEvents(background.Address, "editor", "agentPlan"));
		Assert.Empty(host.Bridge.PostedEvents(host.WorkspaceSession.Address, "editor", "agentPlan"));
	}
}
