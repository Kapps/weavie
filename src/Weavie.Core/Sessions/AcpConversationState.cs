namespace Weavie.Core.Sessions;

/// <summary>Durable continuation state for the primary conversation or one exact BTW fork.</summary>
public sealed record AcpConversationState {
	/// <summary>The Weavie side identity; empty for the primary conversation.</summary>
	public required string ConversationId { get; init; }
	/// <summary>The exact provider session; null until creation has completed.</summary>
	public required string? SessionId { get; init; }
	/// <summary>The primary turn after which a side conversation is displayed.</summary>
	public required long AnchorTurnNumber { get; init; }
	/// <summary>The initial question displayed on a side conversation.</summary>
	public required string InitialPrompt { get; init; }
	/// <summary>The last locally allocated turn in this conversation.</summary>
	public required long TurnNumber { get; init; }
	/// <summary>Whether this conversation already received or inherited host guidance.</summary>
	public required bool GuidanceSent { get; init; }
	/// <summary>Persistent plan identities and their original local turns.</summary>
	public required IReadOnlyDictionary<string, string> PlanTurns { get; init; }
	/// <summary>Whether a side conversation was explicitly retired after a terminal failure.</summary>
	public required bool Failed { get; init; }
}
