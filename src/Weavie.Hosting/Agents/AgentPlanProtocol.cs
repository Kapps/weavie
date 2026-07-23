using System.Text.Json;

namespace Weavie.Hosting.Agents;

internal readonly record struct AgentPlan(string Id, string Title, string Markdown);

/// <summary>Serializes the read-only editor document for one completed agent plan.</summary>
internal static class AgentPlanProtocol {
	public static string Show(AgentPlan plan) {
		ArgumentException.ThrowIfNullOrEmpty(plan.Id);
		ArgumentException.ThrowIfNullOrEmpty(plan.Title);
		ArgumentNullException.ThrowIfNull(plan.Markdown);
		return JsonSerializer.Serialize(new {
			type = "show-agent-plan",
			id = plan.Id,
			title = plan.Title,
			markdown = plan.Markdown,
		});
	}
}
