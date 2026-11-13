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

		fixture.Session.SetCardCollapsed(new(side, null), collapsed: true);
		var collapsed = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-collapsed");
		fixture.Session.SetCardCollapsed(new(side, null), collapsed: false);
		fixture.Session.SetCardCollapsed(new(side, null), collapsed: true);

		Assert.Equal(side, collapsed.ConversationId);
		Assert.Equal(started.AnchorTurnId, collapsed.AnchorTurnId);
		Assert.Equal(["collapsed", "expanded", "collapsed"], fixture.Sessions.ReadMessages("fake", fixture.Workspace)
			.Where(message => message.Type == "side-conversation-collapsed" && message.ConversationId == side)
			.Select(message => message.Status));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new("missing", null), collapsed: true));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new(null, null), collapsed: true));
	}

	[Fact]
	public async Task ADashboardsCollapseIsJournalledInItsOwnConversation() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		string chart = AcpDashboardTests.Chart("p95");
		await fixture.TurnAsync("block:claude:showBlock:" + chart, 1);
		fixture.AskAside("block:claude:showBlock:" + chart);
		var side = await fixture.WaitForMessageAsync(message => message.ConversationId is not null && message.ItemType == Weavie.Core.Agents.AgentDashboards.ItemType);

		fixture.Session.SetCardCollapsed(new(null, "rollout"), collapsed: true);
		fixture.Session.SetCardCollapsed(new(side.ConversationId, "rollout"), collapsed: false);

		var rows = fixture.Sessions.ReadMessages("fake", fixture.Workspace).Where(message => message.Type == Weavie.Core.Agents.AgentDashboards.Collapsed).ToList();
		Assert.Equal([(null, "collapsed"), (side.ConversationId, "expanded")], rows.Select(row => (row.ConversationId, row.Status)));
		Assert.All(rows, row => Assert.Equal(("dashboard:rollout", (string?)null), (row.ItemId, row.Summary)));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new(null, "missing"), collapsed: true));
	}
}
