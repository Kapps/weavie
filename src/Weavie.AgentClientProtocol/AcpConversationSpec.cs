using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>What every conversation of one ACP session shares with its owner.</summary>
internal sealed record AcpConversationHost(
	AgentSessionContext Context,
	Func<AcpAgentDefinition> Definition,
	Action<string> Log,
	Lock TransitionGate);

/// <summary>What a conversation incarnation starts from, how it opens its provider session, and which role it plays.</summary>
internal sealed record AcpConversationSpec(
	AcpConversationHandoff Seed,
	AcpConversationOpening Opening,
	AcpConversationRole Role) {
	/// <summary>Whether this is a <c>/btw</c> side conversation scoped to its own requests.</summary>
	public bool Side => Role == AcpConversationRole.Side;
}

/// <summary>What a conversation is to its session: the primary, a <c>/btw</c> side, or a read-only subagent.</summary>
internal enum AcpConversationRole { Primary, Side, Subagent }

/// <summary>What a retired conversation leaves for its successor; interaction identities stay unique in its pane.</summary>
internal sealed record AcpConversationHandoff(
	AcpConversationState Continuation,
	IReadOnlyList<AgentTurnSubmission> Pending,
	IReadOnlyCollection<string> ResolvedRequests,
	long AuthenticationSequence) {
	/// <summary>A conversation starting from <paramref name="continuation"/> with no history in its pane.</summary>
	public static AcpConversationHandoff Fresh(AcpConversationState continuation) => new(continuation, [], [], 0);
}

/// <summary>How a conversation opens its provider session.</summary>
internal abstract record AcpConversationOpening {
	/// <summary>Loads or resumes the continuation's session, or creates one when it has none.</summary>
	public static AcpConversationOpening Continue { get; } = new ContinueOpening();

	private sealed record ContinueOpening : AcpConversationOpening;
}

/// <summary>Forks the live parent's provider session, at the agent message <paramref name="MessageId"/> when set.</summary>
internal sealed record ForkFromOpening(AcpConversation Parent, string? MessageId) : AcpConversationOpening;

/// <summary>Takes over the subagent session <paramref name="SessionId"/> a parent announced; it never opens, prompts, or closes.</summary>
internal sealed record AdoptedOpening(string SessionId, AcpBackgroundWork Work, string? ParentConversationId) : AcpConversationOpening;

/// <summary>A conversation's display-facing control state.</summary>
internal sealed record AcpConversationSnapshot(
	bool Ready,
	IReadOnlyList<AgentControlAxis> Axes,
	IReadOnlyList<AgentSlashEntry> Commands,
	long TurnNumber);
