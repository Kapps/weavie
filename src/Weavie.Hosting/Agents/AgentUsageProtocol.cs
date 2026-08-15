using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal static class AgentUsageProtocol {
	public static object Message(AgentContextWindowUsage? context) => new {
		state = context is { } window
			? new { usedTokens = window.UsedTokens, capacityTokens = window.CapacityTokens }
			: null,
	};
}
