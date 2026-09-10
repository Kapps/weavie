using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentPaneRecoveryTests {
	[Fact]
	public void RecoveryPreservesPartialSideOutputAndClosesOnlyUnfinishedActivity() {
		var message = new AgentPaneMessage {
			Type = "turn-started",
			ProviderId = "provider",
			ThreadId = "child",
			ConversationId = "btw",
			TurnId = "2",
		};
		var messages = new[] {
			message,
			message with { Type = "item-started", ItemId = "tool", ItemType = "tool" },
			message with { Type = "agent-message-delta", ItemId = "answer", ItemType = "agentMessage", Text = "partial " },
			message with { Type = "agent-message-delta", ItemId = "answer", ItemType = "agentMessage", Text = "answer" },
			message with { Type = "approval-requested", RequestId = "pending" },
			message with { Type = "input-requested", RequestId = "resolved" },
			message with { Type = "input-resolved", RequestId = "resolved", Status = "accepted" },
		};

		var recovery = AgentPaneRecovery.Interrupt(messages);
		Assert.Equal("partial answer", Assert.Single(recovery, update => update.ItemId == "answer").Text);
		Assert.Equal("cancelled", Assert.Single(recovery, update => update.ItemId == "tool").Status);
		Assert.Equal("approval-resolved", Assert.Single(recovery, update => update.RequestId == "pending").Type);
		Assert.DoesNotContain(recovery, update => update.RequestId == "resolved");
		Assert.Equal("btw", Assert.Single(recovery, update => update.Type == "turn-completed").ConversationId);
		Assert.Empty(AgentPaneRecovery.Interrupt([.. messages, .. recovery]));
	}

	[Fact]
	public void AnInterruptedForkOpeningDoesNotLeaveAResumedCardPermanentlyBusy() {
		var opening = new AgentPaneMessage {
			Type = "side-conversation-started",
			ProviderId = "provider",
			ConversationId = "btw",
			Status = "forking",
			Text = "why?",
		};
		var recovery = Assert.Single(AgentPaneRecovery.Interrupt([opening]));
		Assert.Equal("turn-completed", recovery.Type);
		Assert.Equal("cancelled", recovery.Status);
		Assert.Empty(AgentPaneRecovery.Interrupt([opening, recovery]));
	}

}
