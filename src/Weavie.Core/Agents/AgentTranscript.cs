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

/// <summary>One provider-neutral entry of a conversation.</summary>
/// <param name="Role">Who produced the entry.</param>
/// <param name="Text">Its text, or a tool's summary.</param>
/// <param name="Status">The provider's status for the entry, when reported.</param>
/// <param name="From">The id of the session that sent a user entry, when another session's agent sent it.</param>
public sealed record AgentTranscriptEntry(AgentTranscriptRole Role, string Text, string? Status, string? From);

/// <summary>Projects conversation rows onto prompts, replies, tools, and errors.</summary>
public static class AgentTranscript {
	/// <summary>Folds the already-scoped <paramref name="journal"/> and omits thoughts and UI metadata.</summary>
	public static IReadOnlyList<AgentTranscriptEntry> FromPaneJournal(IReadOnlyList<AgentPaneMessage> journal) {
		var ledger = AgentPaneLedger.Of(journal);
		var entries = new List<AgentTranscriptEntry>();
		for (int index = 0; index < ledger.Count; index++) {
			if (Project(ledger.EntryAt(index).Message) is { } entry) entries.Add(entry);
		}
		return entries;
	}

	private static AgentTranscriptEntry? Project(AgentPaneMessage message) {
		return message switch {
			{ Type: "user-message" or "user-command" or "user-steer", Text: { Length: > 0 } text } =>
				new(AgentTranscriptRole.User, text, null, message.Sender?.Id),
			{ ItemType: "agentMessage", Text: { Length: > 0 } text } => new(AgentTranscriptRole.Agent, text, message.Status, null),
			{ ItemType: "tool", Summary: { } summary } => new(AgentTranscriptRole.Tool, summary, message.Status, null),
			{ Type: "error", Text: { } text } => new(AgentTranscriptRole.Error, text, null, null),
			_ => null,
		};
	}
}
