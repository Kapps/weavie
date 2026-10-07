namespace Weavie.Hosting.Agents;

internal readonly record struct AgentPlan(string Id, string Title, string Markdown);

/// <summary>Serializes the read-only editor document for one completed agent plan.</summary>
internal static class AgentPlanProtocol {
	public static string Path(AgentPlan plan) {
		ArgumentException.ThrowIfNullOrEmpty(plan.Id);
		return $"agent-plan:{plan.Id}";
	}

	public static AgentPlanShown Show(AgentPlan plan, string path) {
		ArgumentException.ThrowIfNullOrEmpty(plan.Id);
		ArgumentException.ThrowIfNullOrEmpty(plan.Title);
		ArgumentNullException.ThrowIfNull(plan.Markdown);
		ArgumentException.ThrowIfNullOrEmpty(path);
		return new(plan.Id, path, plan.Title, plan.Markdown);
	}
}

internal sealed record AgentPlanShown(string Id, string Path, string Title, string Markdown);

internal sealed record AgentPlanRemoved(string Path);
