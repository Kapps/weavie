using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentPaneLedgerTests {
	[Fact]
	public void ScopedRowsKeepIdenticalProviderThreadAndItemIdsSeparate() {
		var row = new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "first",
			ConversationId = "child",
			ThreadId = "thread",
			TurnId = "turn",
			ItemId = "item",
			ItemType = "agentMessage",
			Text = "one",
		};
		var ledger = AgentPaneLedger.Of([row, row with { ProviderId = "second", Text = "two" },
			row with { ConversationId = "sibling", Text = "three" }]);
		ledger.Store(row with { Text = "!" });
		Assert.Equal("one!", Assert.Single(ledger.ConversationRows("first", "child", active: true)).Text);
		Assert.Equal("two", Assert.Single(ledger.ConversationRows("second", "child", active: true)).Text);
		Assert.Equal("three", Assert.Single(ledger.ConversationRows("first", "sibling", active: true)).Text);
		ledger.Store(row with { Type = "item-retracted" });
		Assert.Empty(ledger.ConversationRows("first", "child", active: true));
		Assert.Single(ledger.ConversationRows("first", "child", active: false));
		Assert.Equal(2, ledger.ActiveItems.Count);
		ledger.Store(row with { Type = "subagent-started", ItemId = null, ParentItemId = "btw" });
		Assert.Equal("btw", ledger.ParentConversation("first", "child"));
		ledger.Clear();
		Assert.Empty(ledger.ConversationRows("first", "child", active: false));
		Assert.Null(ledger.ParentConversation("first", "child"));
	}

	[Fact]
	public void RecoveryUsesTheLiveFoldIncludingInitialTextAndFirstMediaMetadata() {
		var start = new AgentPaneMessage {
			Type = "item-started",
			ProviderId = "provider",
			ThreadId = "thread",
			TurnId = "turn",
			ItemId = "answer",
			ItemType = "agentMessage",
			Text = "first ",
			MediaType = "image/png",
			MediaData = "first-image",
			ResourceUri = "file:///first",
			MessageId = "message",
		};
		var messages = new[] {
			start,
			start with { Type = "agent-message-delta", Text = "second", MediaData = "later-image", MessageId = null },
			start with { Type = "agent-message-delta", Text = "!", MediaType = null, MediaData = null, ResourceUri = null },
		};
		var ledger = AgentPaneLedger.Of(messages);
		var row = Assert.Single(ledger.ActiveItems);
		Assert.Equal("first second!", row.Text);
		Assert.Equal("first-image", row.MediaData);
		Assert.Equal("image/png", row.MediaType);
		Assert.Equal("file:///first", row.ResourceUri);
		Assert.Equal("message", row.MessageId);
		Assert.Equal(row with { Type = "item-completed", Status = "cancelled" }, Assert.Single(AgentPaneRecovery.Interrupt(messages)));
		ledger.Store(row with { Type = "item-completed" });
		Assert.Empty(ledger.ActiveItems);
		ledger.Store(start with { Type = "agent-message-delta", Text = "new segment" });
		Assert.Equal("new segment", Assert.Single(ledger.ActiveItems).Text);
	}
}
