using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentTranscriptTests {
	[Fact]
	public void FoldsTheJournalIntoPrimaryPromptsRepliesToolsAndErrors() {
		var entries = AgentTranscript.FromPaneJournal([
			Message("turn-started", null, null),
			Message("user-message", "prompt-1", null) with { Text = "Fix it" },
			Message("thought-message-delta", "thought:1", "thought") with { Text = "hmm" },
			Message("item-started", "tool:1", "tool") with { Summary = "grep foo", Status = "inProgress" },
			Message("item-completed", "tool:1", "tool") with { Summary = "grep foo", Status = "completed" },
			Message("agent-message-delta", "agentMessage:1", "agentMessage") with { Text = "Fixed " },
			Message("agent-message-delta", "agentMessage:1", "agentMessage") with { Text = "it." },
			Message("user-steer", "prompt-2", null) with { Text = "Also test", Sender = new("feat/other", "Other") },
			Message("agent-message-delta", "agentMessage:2", "agentMessage") with { Text = "side", ConversationId = "side-1" },
			Message("error", null, null) with { Text = "Agent crashed" },
			Message("turn-completed", null, null),
		]);

		Assert.Equal([
			new(AgentTranscriptRole.User, "Fix it", null, null),
			new(AgentTranscriptRole.Tool, "grep foo", "completed", null),
			new(AgentTranscriptRole.Agent, "Fixed it.", null, null),
			new(AgentTranscriptRole.User, "Also test", null, "feat/other"),
			new AgentTranscriptEntry(AgentTranscriptRole.Error, "Agent crashed", null, null),
		], entries);
	}

	[Fact]
	public void ACompletedReplyReplacesItsStream() {
		var entries = AgentTranscript.FromPaneJournal([
			Message("agent-message-delta", "agentMessage:1", "agentMessage") with { Text = "Draf" },
			Message("item-completed", "agentMessage:1", "agentMessage") with { Text = "Final", Status = "completed" },
		]);

		Assert.Equal([new AgentTranscriptEntry(AgentTranscriptRole.Agent, "Final", "completed", null)], entries);
	}

	private static AgentPaneMessage Message(string type, string? itemId, string? itemType) => new() {
		Type = type,
		ProviderId = "codex",
		ThreadId = "thread",
		TurnId = "1",
		ItemId = itemId,
		ItemType = itemType,
	};
}
