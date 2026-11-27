using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideCollapseTests {
	[Fact]
	public async Task CollapseIsJournalledAndRestoredWithTheSideConversation() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.AskAside("hello");
		var started = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		string side = started.ConversationId!;
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId == side);

		fixture.Session.SetCardCollapsed(new(side, null, null), collapsed: true);
		var collapsed = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-collapsed");
		fixture.Session.SetCardCollapsed(new(side, null, null), collapsed: false);
		fixture.Session.SetCardCollapsed(new(side, null, null), collapsed: true);

		Assert.Equal(side, collapsed.ConversationId);
		Assert.Equal(started.AnchorTurnId, collapsed.AnchorTurnId);
		Assert.Equal(["collapsed", "expanded", "collapsed"], fixture.Sessions.ReadMessages("fake", fixture.Workspace)
			.Where(message => message.Type == "side-conversation-collapsed" && message.ConversationId == side)
			.Select(message => message.Status));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new("missing", null, null), collapsed: true));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new(null, null, null), collapsed: true));
	}

	[Fact]
	public async Task ADashboardsCollapseIsJournalledInItsOwnConversation() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		string chart = AcpDashboardTests.Chart("p95");
		await fixture.TurnAsync("block:claude:showBlock:" + chart, 1);
		fixture.AskAside("block:claude:showBlock:" + chart);
		var side = await fixture.WaitForMessageAsync(message => message.ConversationId is not null && message.ItemType == Weavie.Core.Agents.AgentDashboards.ItemType);

		fixture.Session.SetCardCollapsed(new(null, "rollout", null), collapsed: true);
		fixture.Session.SetCardCollapsed(new(side.ConversationId, "rollout", null), collapsed: false);

		var rows = fixture.Sessions.ReadMessages("fake", fixture.Workspace).Where(message => message.Type == Weavie.Core.Agents.AgentDashboards.Collapsed).ToList();
		Assert.Equal([(null, "collapsed"), (side.ConversationId, "expanded")], rows.Select(row => (row.ConversationId, row.Status)));
		Assert.All(rows, row => Assert.Equal(("dashboard:rollout", (string?)null), (row.ItemId, row.Summary)));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new(null, "missing", null), collapsed: true));
	}

	[Fact]
	public async Task APromptKeepsItsAnchorAndItsReplyCardsStateIsJournalled() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		var anchor = new AgentAnchor("/w/a.cs", null, null, "3.1-4.9", "int b;", "ask-1");
		fixture.Session.Submit(AgentTurnSubmission.Prompt("Why?") with { Id = "ask-1", Anchor = anchor });
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		fixture.Session.SetReplyCard("ask-1", AgentReplyCardState.Collapsed);
		fixture.Session.SetReplyCard("ask-1", AgentReplyCardState.Dismissed);

		var rows = fixture.Sessions.ReadMessages("fake", fixture.Workspace);
		Assert.Equal(anchor, Assert.Single(rows, row => row.Type == "user-message").Anchor);
		Assert.Equal(["collapsed", "dismissed"], rows.Where(row => row.Type == "reply-card" && row.ItemId == "ask-1").Select(row => row.Status));
	}
}
