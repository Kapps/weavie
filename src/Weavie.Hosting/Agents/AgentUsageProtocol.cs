using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal static class AgentUsageProtocol {
	public static object Message(AgentUsageSnapshot usage) {
		ArgumentNullException.ThrowIfNull(usage);
		return new {
			state = new {
				contextWindow = usage.ContextWindow is { } window
					? new { usedTokens = window.UsedTokens, capacityTokens = window.CapacityTokens }
					: null,
				limits = usage.Limits.Select(limit => new {
					id = limit.Id,
					status = limit.Status.ToString().ToLowerInvariant(),
					usedPercent = limit.UsedPercent,
					resetsAtMs = limit.ResetsAt?.ToUnixTimeMilliseconds(),
				}),
			},
		};
	}
}
