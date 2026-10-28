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
		string plan = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "plans", "claude.md");
		Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
		File.WriteAllText(plan, "# Plan\n");
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt + plan);
		var open = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "openFile"));
		Assert.Equal((plan, "background"), (open.GetProperty("path").GetString(), open.GetProperty("intent").GetString()));
		var review = Assert.Single(Reviews(host, session));
		Assert.Equal((plan, true), (review.GetProperty("path").GetString(), review.GetProperty("editable").GetBoolean()));
		Assert.Equal(["allow", "reject"], review.GetProperty("actions").EnumerateArray().Select(action => action.GetProperty("id").GetString()));
		Assert.Same(host.WorkspaceSession, host.SelectedSession);

		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(session, "lifecycle", "sync", new { });
		Assert.Single(Reviews(host, session));
		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt);
		Assert.Equal(2, Reviews(host, session).Length);
		Assert.Empty(host.Bridge.PostedEvents(session.Address, "editor", "openFile"));
		Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "openOverlay"));

		host.SessionEvent(session, "agent", "permission", new { requestId = review.GetProperty("requestId").GetString(), optionId = "allow" });
		Assert.False(Assert.Single(Reviews(host, session)).GetProperty("editable").GetBoolean());
	}

	[Fact]
	public async Task TextReview_OpensItsPlanAsAReadOnlyDocument() {
		await using var host = await TestHost.StartAsync();
		var session = await CreateAsync(host, "text-plan-review");
		host.Bridge.Clear();

		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt);
		var shown = Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "agentPlan"));
		Assert.Equal("background", Assert.Single(host.Bridge.PostedEvents(session.Address, "editor", "openOverlay")).GetProperty("intent").GetString());
		var review = Assert.Single(Reviews(host, session));
		Assert.Equal((shown.GetProperty("path").GetString(), false),
			(review.GetProperty("path").GetString(), review.GetProperty("editable").GetBoolean()));
		Assert.Equal(FakeStructuredAgentProvider.PlanMarkdown, shown.GetProperty("markdown").GetString());
	}

	[Fact]
	public async Task AutoOpenOff_PublishesTheReviewWithoutOpeningIt() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PlanReviewAutoOpen, JsonSerializer.SerializeToElement(false));
		var session = await CreateAsync(host, "closed-plan-review");
		host.Bridge.Clear();

		Submit(host, session, FakeStructuredAgentProvider.PlanReviewPrompt);
		Assert.Single(Reviews(host, session));
		Assert.Null(host.Bridge.LastEvent(session.Address, "editor", "agentPlan"));
		Assert.Null(host.Bridge.LastEvent(session.Address, "editor", "openOverlay"));
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
