using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal static class AgentBackgroundProtocol {
	public static AgentBackgroundMessage Message(IReadOnlyList<AgentBackgroundItem> items) {
		ArgumentNullException.ThrowIfNull(items);
		return new([.. items.Select(item => new AgentBackgroundWire(
			item.Id,
			item.Kind.ToString().ToLowerInvariant(),
			item.Name,
			item.Type,
			item.Detail,
			item.LastActivity,
			item.Usage,
			item.State.ToString().ToLowerInvariant(),
			item.CanStop,
			item.StartedAtMs,
			item.EndedAtMs,
			item.TranscriptItemId))]);
	}
}

internal sealed record AgentBackgroundMessage(IReadOnlyList<AgentBackgroundWire> Items);

internal sealed record AgentBackgroundWire(
	string Id,
	string Kind,
	string Name,
	string Type,
	string? Detail,
	string? LastActivity,
	AgentBackgroundUsage? Usage,
	string State,
	bool CanStop,
	long StartedAtMs,
	long? EndedAtMs,
	string? TranscriptItemId);
