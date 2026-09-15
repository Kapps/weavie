using Weavie.Core.Editor;
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

	private void SubscribeAgentPlanDocuments(EditorSession session) {
		string[] paths = [.. session.Open.Where(tab => tab.Kind == "plan").Select(tab => tab.Path)
			.Except(AgentPlanDocumentPaths(), StringComparer.Ordinal)];
		if (paths.Length == 0) return;
		Agent.WithPlanDocuments(plans => {
			var documents = plans.ToDictionary(AgentPlanProtocol.Path, StringComparer.Ordinal);
			foreach (string path in paths.Except(AgentPlanDocumentPaths(), StringComparer.Ordinal)) {
				PublishAgentPlanDocument(path, documents);
			}
		});
	}

	private void UpdateAgentPlans(IReadOnlyList<AgentPlan> plans) {
		var documents = plans.ToDictionary(AgentPlanProtocol.Path, StringComparer.Ordinal);
		foreach (string path in AgentPlanDocumentPaths()) {
			PublishAgentPlanDocument(path, documents);
		}
	}

	private IEnumerable<string> AgentPlanDocumentPaths() =>
		State.Keys("editor", "agentPlan").Concat(State.Keys("editor", "agentPlanRemoved"));

	private void PublishAgentPlanDocument(string path, IReadOnlyDictionary<string, AgentPlan> documents) {
		if (documents.TryGetValue(path, out var plan)) {
			State.Set("editor", path, "agentPlan", AgentPlanProtocol.Show(plan, path));
		} else {
			State.Set("editor", path, "agentPlanRemoved", new { path });
		}
	}
}
