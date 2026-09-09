using Weavie.Hosting.Agents;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	/// <summary>Reveals the exact agent plan and keeps its editor document current.</summary>
	public bool OpenAgentPlan(string threadId, string turnId, string itemId) =>
		Agent.WithCompletedPlan(threadId, turnId, itemId, plan => {
			string path = AgentPlanProtocol.Path(plan);
			State.Set("editor", path, "agentPlan", AgentPlanProtocol.Show(plan, path));
			OpenEditorOverlay(path, "plan");
		});

	private void UpdateAgentPlans(IReadOnlyList<AgentPlan> plans) {
		var documents = plans.ToDictionary(AgentPlanProtocol.Path, StringComparer.Ordinal);
		foreach (var tab in EditorSession.Open.Where(tab => tab.Kind == "plan")) {
			if (documents.TryGetValue(tab.Path, out var plan)) {
				State.Set("editor", tab.Path, "agentPlan", AgentPlanProtocol.Show(plan, tab.Path));
			} else {
				State.Set("editor", tab.Path, "agentPlanRemoved", new { path = tab.Path });
			}
		}
	}
}
