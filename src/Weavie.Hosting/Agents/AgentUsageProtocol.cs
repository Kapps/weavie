using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal static class AgentUsageProtocol {
	public static object Message(AgentUsageState state) {
		ArgumentNullException.ThrowIfNull(state);
		return new {
			state = new {
				contextWindow = state.ContextWindow is { } context ? new {
					usedTokens = context.UsedTokens,
					capacityTokens = context.CapacityTokens,
				} : null,
				totalTokens = state.TotalTokens,
				rateLimits = state.RateLimits.Select(limit => new {
					id = limit.Id,
					label = limit.Label,
					usedPercent = limit.UsedPercent,
					windowMinutes = limit.WindowMinutes,
					resetsAtMs = limit.ResetsAt?.ToUnixTimeMilliseconds(),
				}),
			},
		};
	}
}
