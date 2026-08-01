namespace Weavie.Hosting.Agents;

internal readonly record struct AgentPlan(string Id, string Title, string Markdown);

/// <summary>Serializes the read-only editor document for one completed agent plan.</summary>
internal static class AgentPlanProtocol {
	public static string Path(AgentPlan plan) {
		ArgumentException.ThrowIfNullOrEmpty(plan.Id);
		return $"agent-plan:{plan.Id}";
	}

	public static object Show(AgentPlan plan, string path) {
		ArgumentException.ThrowIfNullOrEmpty(plan.Id);
		ArgumentException.ThrowIfNullOrEmpty(plan.Title);
		ArgumentNullException.ThrowIfNull(plan.Markdown);
		ArgumentException.ThrowIfNullOrEmpty(path);
		return new {
			id = plan.Id,
			path,
			title = plan.Title,
			markdown = plan.Markdown,
		};
	}
}
