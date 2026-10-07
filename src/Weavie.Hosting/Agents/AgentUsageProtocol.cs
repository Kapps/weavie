using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal static class AgentUsageProtocol {
	public static AgentUsageMessage Message(AgentUsageSnapshot usage) {
		ArgumentNullException.ThrowIfNull(usage);
		return new(new(
			usage.ContextWindow,
			[.. usage.Limits.Select(limit => new AgentUsageLimitWire(
				limit.Id,
				limit.Status.ToString().ToLowerInvariant(),
				limit.UsedPercent,
				limit.ResetsAt?.ToUnixTimeMilliseconds()))]));
	}
}

internal sealed record AgentUsageMessage(AgentUsageState State);

internal sealed record AgentUsageState(AgentContextWindowUsage? ContextWindow, IReadOnlyList<AgentUsageLimitWire> Limits);

internal sealed record AgentUsageLimitWire(string Id, string Status, double? UsedPercent, long? ResetsAtMs);
