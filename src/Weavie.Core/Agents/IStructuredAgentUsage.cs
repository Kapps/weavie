namespace Weavie.Core.Agents;

/// <summary>A structured session's current context-window consumption.</summary>
public sealed record AgentContextWindowUsage(long UsedTokens, long CapacityTokens);

/// <summary>Optional live usage capability for structured providers that expose authoritative data.</summary>
public interface IStructuredAgentUsage {
	/// <summary>The latest context-window snapshot, or null until the provider reports one.</summary>
	AgentContextWindowUsage? ContextUsage { get; }

	/// <summary>Raised whenever authoritative context usage changes.</summary>
	event Action<AgentContextWindowUsage?> ContextUsageChanged;
}
