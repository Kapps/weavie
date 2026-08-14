namespace Weavie.Core.Agents;

/// <summary>A structured session's current context-window consumption.</summary>
public sealed record AgentContextWindowUsage(long UsedTokens, long CapacityTokens);

/// <summary>One provider usage-limit window.</summary>
public sealed record AgentRateLimitUsage(
	string Id,
	string? Label,
	double UsedPercent,
	long? WindowMinutes,
	DateTimeOffset? ResetsAt);

/// <summary>Provider-neutral usage reported for one structured agent session.</summary>
public sealed record AgentUsageState(
	AgentContextWindowUsage? ContextWindow,
	long? TotalTokens,
	IReadOnlyList<AgentRateLimitUsage> RateLimits);

/// <summary>Optional live usage capability for structured providers that expose authoritative data.</summary>
public interface IStructuredAgentUsage {
	/// <summary>The latest usage snapshot.</summary>
	AgentUsageState UsageState { get; }

	/// <summary>Raised whenever authoritative usage changes.</summary>
	event Action<AgentUsageState> UsageStateChanged;
}
