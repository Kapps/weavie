using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreEditorSessionRevisionTests {
	[Fact]
	public async Task SnapshotTakenBeforeAHostOpen_KeepsTheTabAndItsPlanRevision() {
		await using var host = await StartAsync("stale-open");
		var session = host.Session("stale-open");
		Submit(host, session, FakeStructuredAgentProvider.PlanPrompt);
		long before = host.Bridge.EditorRevision(session.Address);
		Assert.True(session.OpenAgentPlan("thread-fake", "turn-1", "plan-1"));
		string path = session.EditorSession.Active!;

		host.SessionEvent(session, "editor", "sessionChanged", new { session = Empty(), basis = before });
		Submit(host, session, FakeStructuredAgentProvider.PlanRevisionPrompt);

		Assert.Contains(session.EditorSession.Open, tab => tab.Path == path);
		var plan = host.Bridge.LastEvent(session.Address, "editor", "agentPlan")!.Value;
		Assert.Equal(FakeStructuredAgentProvider.RevisedPlanMarkdown, plan.GetProperty("markdown").GetString());
	}

	[Fact]
	public async Task SnapshotTakenBeforeAHostClose_DoesNotReopenTheTab() {
		await using var host = await StartAsync("stale-close");
		var session = host.Session("stale-close");
		Submit(host, session, FakeStructuredAgentProvider.PlanPrompt);
		Assert.True(session.OpenAgentPlan("thread-fake", "turn-1", "plan-1"));
		string path = session.EditorSession.Active!;
		long opened = host.Bridge.EditorRevision(session.Address);

		await session.DiffPresenter.CloseTabAsync(path, CancellationToken.None);
		host.SessionEvent(session, "editor", "sessionChanged", new { session = WithPlan(path), basis = opened });
		Assert.Empty(session.EditorSession.Open);

		host.SessionEvent(session, "editor", "sessionChanged", new { session = WithPlan(path) });
		Assert.Equal(path, Assert.Single(session.EditorSession.Open).Path);
	}

	[Fact]
	public async Task ClosingATabThatIsNotOpen_SendsNoEdit() {
		await using var host = await StartAsync("noop-close");
		var session = host.Session("noop-close");
		long before = host.Bridge.EditorRevision(session.Address);
		host.Bridge.Clear();

		await session.DiffPresenter.CloseTabAsync("/not-open.ts", CancellationToken.None);

		Assert.Empty(host.Bridge.PostedEvents(session.Address, "editor", "closeTab"));
		Assert.Equal(before, host.Bridge.EditorRevision(session.Address));
	}

	private static async Task<TestHost> StartAsync(string branch) {
		var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AgentProviderId = "structured",
		})).Ok);
		return host;
	}

	private static object Empty() => new { active = (string?)null, open = Array.Empty<object>() };

	private static object WithPlan(string path) =>
		new { active = path, open = new[] { new { path, kind = "plan", viewState = (object?)null } } };

	private static void Submit(TestHost host, HostSession session, string prompt) =>
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt, kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });
}
