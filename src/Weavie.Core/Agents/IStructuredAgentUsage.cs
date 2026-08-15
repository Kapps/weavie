namespace Weavie.Core.Agents;

/// <summary>A structured session's current context-window consumption.</summary>
public sealed record AgentContextWindowUsage(long UsedTokens, long CapacityTokens);

/// <summary>How much headroom a provider usage window has left.</summary>
public enum AgentUsageLimitStatus {
	/// <summary>Within the window's allowance.</summary>
	Allowed,

	/// <summary>Approaching the window's allowance.</summary>
	Warning,

	/// <summary>The window's allowance is spent.</summary>
	Exhausted,
}

/// <summary>One provider usage window, identified as the provider names it.</summary>
public sealed record AgentUsageLimit(
	string Id,
	AgentUsageLimitStatus Status,
	double? UsedPercent,
	DateTimeOffset? ResetsAt);

/// <summary>Everything one structured session reports about what it has consumed.</summary>
public sealed record AgentUsageSnapshot(
	AgentContextWindowUsage? ContextWindow,
	IReadOnlyList<AgentUsageLimit> Limits);

/// <summary>Optional live usage capability for structured providers that expose authoritative data.</summary>
public interface IStructuredAgentUsage {
	/// <summary>The latest usage snapshot; empty until the provider reports one.</summary>
	AgentUsageSnapshot Snapshot { get; }

	/// <summary>Raised whenever authoritative usage changes.</summary>
	event Action<AgentUsageSnapshot> UsageChanged;
}
