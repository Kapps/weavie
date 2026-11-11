using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCorePlanReviewTests {
	[Fact]
	public async Task FileReview_OpensItsPlanOnceAndPublishesItUntilAnswered() {
		await using var host = await TestHost.StartAsync();
		var session = await CreateAsync(host, "plan-review");
		string first = PlanFile(host, "first.md");
		string second = PlanFile(host, "second.md");
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt + first);
		var open = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "openFile"));
		Assert.Equal((first, "background"), (open.GetProperty("path").GetString(), open.GetProperty("intent").GetString()));
		var review = Assert.Single(Reviews(host, session));
		Assert.Equal(first, review.GetProperty("path").GetString());
		Assert.Equal(["allow", "reject"], review.GetProperty("actions").EnumerateArray().Select(action => action.GetProperty("id").GetString()));
		Assert.Same(host.WorkspaceSession, host.SelectedSession);

		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(session, "lifecycle", "sync", new { });
		Assert.Single(Reviews(host, session));
		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt + second);
		Assert.Equal(2, Reviews(host, session).Length);
		Assert.Equal(second, Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "openFile")).GetProperty("path").GetString());

		host.Bridge.Clear();
		Assert.True(session.OpenAgentPlan("structured", null, "thread-fake", "turn-1", "request:review-1"));
		var reopened = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "openFile"));
		Assert.Equal((first, "navigation"), (reopened.GetProperty("path").GetString(), reopened.GetProperty("intent").GetString()));

		host.SessionEvent(session, "agent", "permission", new { requestId = review.GetProperty("requestId").GetString(), optionId = "allow" });
		Assert.Equal(second, Assert.Single(Reviews(host, session)).GetProperty("path").GetString());
	}

	[Fact]
	public async Task AutoOpenOff_PublishesTheReviewWithoutOpeningIt() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PlanReviewAutoOpen, JsonSerializer.SerializeToElement(false));
		var session = await CreateAsync(host, "closed-plan-review");
		host.Bridge.Clear();

		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt + PlanFile(host, "plan.md"));
		Assert.Single(Reviews(host, session));
		Assert.Null(host.Bridge.LastEvent(session.Address, "editor", "openFile"));
	}

	private static string PlanFile(TestHost host, string name) {
		string path = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "plans", name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "# Plan\n");
		return path;
	}

	private static async Task<HostSession> CreateAsync(TestHost host, string branch) {
		var created = await host.CreateSessionAsync(new NewSessionRequest { Branch = branch, Base = "main", AgentProviderId = "structured" });
		Assert.True(created.Ok, created.Error);
		return host.Session(branch);
	}

	private static JsonElement[] Reviews(TestHost host, HostSession session) =>
		host.Bridge.LastEvent(session.Address, "planReview", "reviews") is { } state
			? [.. state.GetProperty("reviews").EnumerateArray()]
			: [];

	private static void Submit(TestHost host, HostSession session, string prompt) =>
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt, kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });
}
