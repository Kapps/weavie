using Weavie.Core.Mcp;

namespace Weavie.Core.Agents;

/// <summary>An owned interactive conversation incarnation; disposing it never addresses a successor.</summary>
public interface IAgentConversation : IAsyncDisposable {
	/// <summary>The Weavie identity used at presentation and persistence boundaries.</summary>
	string Id { get; }

	/// <summary>The conversation's passive view in the session's shared output.</summary>
	ConversationView View { get; }

	/// <summary>The controls currently advertised by this conversation.</summary>
	AgentControlState ControlState { get; }

	/// <summary>Completes exactly once when this incarnation ends, including before a consumer starts observing it.</summary>
	Task<AgentConversationOutcome> Completion { get; }

	/// <summary>Whether no opening, delivery, queued input, interaction, or owned background work remains.</summary>
	bool Settled { get; }

	/// <summary>Admits input to this incarnation; it may steer a running turn.</summary>
	void Submit(AgentTurnSubmission submission);

	/// <summary>Interrupts this conversation's running work.</summary>
	void Interrupt();

	/// <summary>Applies exact advertised controls without changing the user's defaults.</summary>
	Task ApplyControlsAsync(IReadOnlyList<AgentControlValue> values);
}

/// <summary>The terminal outcome of a conversation incarnation, separate from the ending of any one turn.</summary>
public sealed record AgentConversationOutcome(string Reason, Exception? Error);

/// <summary>Construction-time context and controls owned by a conversation's consumer.</summary>
public sealed record AgentConversationBinding {
	/// <summary>The registry grant owned by this interactive conversation.</summary>
	public required CapabilityRegistryGrant Tools { get; init; }

	/// <summary>The title shown for the conversation, which is its grant's name.</summary>
	public string Title => Tools.Conversation ?? "Main";

	/// <summary>The consumer's context attached to each delivered turn.</summary>
	public required IAgentTurnContext TurnContext { get; init; }

	/// <summary>The controls required when this conversation opens.</summary>
	public required IAgentControlHold ControlHold { get; init; }
}

/// <summary>Creates independently owned conversations on a structured session's provider runtime.</summary>
public interface IStructuredAgentConversations {
	/// <summary>Opens a fresh conversation and returns only after authentication and required controls succeed.</summary>
	Task<IAgentConversation> OpenFreshAsync(AgentConversationBinding binding, CancellationToken ct);

	/// <summary>Forks the current main incarnation, captured when opening is admitted.</summary>
	Task<IAgentConversation> OpenForkAsync(AgentConversationBinding binding, CancellationToken ct);
}
