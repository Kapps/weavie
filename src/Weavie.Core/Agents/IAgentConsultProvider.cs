namespace Weavie.Core.Agents;

/// <summary>An agent provider another agent can consult for one read-only turn in a worktree.</summary>
public interface IAgentConsultProvider : IAgentProvider {
	/// <summary>Opens one throwaway session and returns its advertised controls; throws with the agent's reason.</summary>
	Task<IReadOnlyList<AgentControlAxis>> ProbeControlsAsync(CancellationToken ct);

	/// <summary>Runs one consult turn. Every non-cancellation failure is returned, never thrown.</summary>
	Task<AgentConsultOutcome> ConsultAsync(AgentConsultRequest request, CancellationToken ct);
}

/// <summary>One consult turn requested by an embedded agent.</summary>
public sealed record AgentConsultRequest {
	/// <summary>The worktree the consulted agent works in.</summary>
	public required string Workspace { get; init; }

	/// <summary>The provider-native model id, or empty to keep the agent's default.</summary>
	public required string Model { get; init; }

	/// <summary>The complete request; the consulted agent sees nothing else of the caller's conversation.</summary>
	public required string Prompt { get; init; }
}

/// <summary>The result of one consult turn.</summary>
public abstract record AgentConsultOutcome {
	/// <summary>The model that answered, or the requested id when no session opened.</summary>
	public required string ModelId { get; init; }

	/// <summary>The controls the consult session advertised; empty when no session opened.</summary>
	public required IReadOnlyList<AgentControlAxis> Controls { get; init; }
}

/// <summary>The consulted agent's final message.</summary>
public sealed record AgentConsultSuccess : AgentConsultOutcome {
	/// <summary>The agent message text after its last tool call.</summary>
	public required string Reply { get; init; }

	/// <summary>The titles of permission requests Weavie denied during the turn.</summary>
	public required IReadOnlyList<string> Denied { get; init; }
}

/// <summary>Why a consult produced no reply.</summary>
public sealed record AgentConsultFailure : AgentConsultOutcome {
	/// <summary>The user-facing cause.</summary>
	public required string Detail { get; init; }
}
