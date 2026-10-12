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

		fixture.Session.SetAsideCollapsed(side, collapsed: true);
		var collapsed = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-collapsed");
		fixture.Session.SetAsideCollapsed(side, collapsed: false);
		fixture.Session.SetAsideCollapsed(side, collapsed: true);

		Assert.Equal(side, collapsed.ConversationId);
		Assert.Equal(started.AnchorTurnId, collapsed.AnchorTurnId);
		Assert.Equal(["collapsed", "expanded", "collapsed"], fixture.Sessions.ReadMessages("fake", fixture.Workspace)
			.Where(message => message.Type == "side-conversation-collapsed" && message.ConversationId == side)
			.Select(message => message.Status));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetAsideCollapsed("missing", collapsed: true));
	}
}
