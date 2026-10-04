using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>What every conversation of one ACP session shares with its owner.</summary>
internal sealed record AcpConversationHost(
	AgentSessionContext Context,
	Func<AcpAgentDefinition> Definition,
	Action<string> Log,
	Lock TransitionGate,
	AcpJsonRpcConnection Connection);

/// <summary>The continuation a conversation incarnation starts from and how it opens its provider session.</summary>
internal sealed record AcpConversationSpec(
	AcpConversationState Continuation,
	IReadOnlyList<AgentTurnSubmission> Pending,
	AcpConversationOpening Opening,
	bool SideScoped);

/// <summary>What a retired conversation leaves for its successor.</summary>
internal sealed record AcpConversationHandoff(
	AcpConversationState Continuation,
	IReadOnlyList<AgentTurnSubmission> Pending);

/// <summary>How a conversation opens its provider session.</summary>
internal abstract record AcpConversationOpening {
	/// <summary>Loads or resumes the continuation's session, or creates one when it has none.</summary>
	public static AcpConversationOpening Continue { get; } = new ContinueOpening();

	private sealed record ContinueOpening : AcpConversationOpening;
}

/// <summary>Forks the parent's provider session; the parent must still be live.</summary>
internal sealed record ForkFromOpening(AcpConversation Parent) : AcpConversationOpening;

/// <summary>A conversation's display-facing control state.</summary>
internal sealed record AcpConversationSnapshot(
	bool Ready,
	IReadOnlyList<AgentControlAxis> Axes,
	IReadOnlyList<AgentSlashEntry> Commands,
	long TurnNumber);
