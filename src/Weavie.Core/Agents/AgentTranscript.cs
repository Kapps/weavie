namespace Weavie.Core.Agents;

/// <summary>Who produced one transcript entry.</summary>
public enum AgentTranscriptRole {
	/// <summary>A prompt the user (or another session) submitted.</summary>
	User,

	/// <summary>Text the agent replied with.</summary>
	Agent,

	/// <summary>A tool the agent ran, by summary; never its output.</summary>
	Tool,

	/// <summary>A failure the provider reported.</summary>
	Error,
}

/// <summary>One provider-neutral entry of a session's primary conversation.</summary>
public sealed record AgentTranscriptEntry(AgentTranscriptRole Role, string Text, string? Status);

/// <summary>Projects a saved pane journal onto the primary conversation's prompts, replies, tools, and errors.</summary>
public static class AgentTranscript {
	/// <summary>Folds <paramref name="journal"/> and keeps primary-thread entries; thoughts and nested conversations are dropped.</summary>
	public static IReadOnlyList<AgentTranscriptEntry> FromPaneJournal(IReadOnlyList<AgentPaneMessage> journal) {
		var ledger = AgentPaneLedger.Of(journal);
		var entries = new List<AgentTranscriptEntry>();
		for (int index = 0; index < ledger.Count; index++) {
			if (Project(ledger.EntryAt(index).Message) is { } entry) entries.Add(entry);
		}
		return entries;
	}

	private static AgentTranscriptEntry? Project(AgentPaneMessage message) {
		if (message.ConversationId is not null) return null;
		return message switch {
			{ Type: "user-message" or "user-command" or "user-steer", Text: { Length: > 0 } text } =>
				new(AgentTranscriptRole.User, text, null),
			{ ItemType: "agentMessage", Text: { Length: > 0 } text } => new(AgentTranscriptRole.Agent, text, message.Status),
			{ ItemType: "tool", Summary: { } summary } => new(AgentTranscriptRole.Tool, summary, message.Status),
			{ Type: "error", Text: { } text } => new(AgentTranscriptRole.Error, text, null),
			_ => null,
		};
	}
}
