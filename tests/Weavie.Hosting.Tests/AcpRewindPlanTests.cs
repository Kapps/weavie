using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpRewindPlanTests {
	[Fact]
	public void CutsAtThePromptAndForksAtThePreviousTurnsLastAnswer() {
		var journal = new[] {
			Turn("1"), User("1", "alpha"), Answer("1", "msg-1a"), Answer("1", "msg-1b"),
			Side("early", "1", "side-msg"),
			Turn("2"), User("2", "bravo"), Answer("2", "msg-2"),
			Side("early", "1", "late-side-reply"), Side("late", "2", "dropped"),
		};

		var plan = AcpRewindPlan.Create(journal, "2");

		Assert.Equal(("bravo", "msg-1b", 2L), (plan.Prompt, plan.ForkMessageId, plan.Turn));
		Assert.Equal([.. journal[..5], journal[8]], plan.Kept);
		Assert.True(plan.Keeps(1));
		Assert.False(plan.Keeps(2));
	}

	[Fact]
	public void RewindingTheFirstPromptKeepsNothingAndNeedsNoFork() {
		var plan = AcpRewindPlan.Create([Turn("1"), User("1", "alpha"), Answer("1", "msg-1")], "1");

		Assert.Equal(("alpha", (string?)null), (plan.Prompt, plan.ForkMessageId));
		Assert.Empty(plan.Kept);
	}

	[Fact]
	public void RefusesWhenThePreviousTurnHasNoAnswerOrThePromptIsGone() {
		AgentPaneMessage[] journal = [Turn("1"), User("1", "alpha"), Turn("2"), User("2", "bravo")];

		Assert.Contains("no recorded agent reply", Assert.Throws<InvalidOperationException>(() => AcpRewindPlan.Create(journal, "2")).Message, StringComparison.Ordinal);
		Assert.Throws<InvalidOperationException>(() => AcpRewindPlan.Create(journal, "3"));
	}

	private static AgentPaneMessage Turn(string turn) => new() { Type = "turn-started", ProviderId = "fake", TurnId = turn };

	private static AgentPaneMessage User(string turn, string text) =>
		new() { Type = "user-message", ProviderId = "fake", TurnId = turn, Text = text };

	private static AgentPaneMessage Answer(string turn, string messageId) => new() {
		Type = "item-completed",
		ProviderId = "fake",
		TurnId = turn,
		ItemType = "agentMessage",
		MessageId = messageId,
	};

	private static AgentPaneMessage Side(string conversation, string anchor, string messageId) =>
		Answer("1", messageId) with { ConversationId = conversation, AnchorTurnId = anchor };
}
