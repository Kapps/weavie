using Weavie.Core.Editor;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private readonly HashSet<string> _openedPlanReviews = new(StringComparer.Ordinal);

	/// <summary>Reveals the exact agent plan and keeps its editor document current.</summary>
	public bool OpenAgentPlan(string threadId, string turnId, string itemId) =>
		Agent.WithCompletedPlan(threadId, turnId, itemId, plan => OpenAgentPlan(plan, AgentPlanProtocol.Path(plan), EditorOpenIntent.Navigation));

	private void OpenAgentPlan(AgentPlan plan, string path, EditorOpenIntent intent) {
		State.Set("editor", path, "agentPlan", WireJson.Default.AgentPlanShown, AgentPlanProtocol.Show(plan, path));
		OpenEditorOverlay(path, "plan", intent);
	}

	private void UpdateAgentPlans(AgentPlansSnapshot plans, bool autoOpen) {
		var documents = plans.Documents.ToDictionary(AgentPlanProtocol.Path, StringComparer.Ordinal);
		foreach (var tab in EditorSession.Open.Where(tab => tab.Kind == "plan")) {
			if (documents.TryGetValue(tab.Path, out var plan)) {
				State.Set("editor", tab.Path, "agentPlan", WireJson.Default.AgentPlanShown, AgentPlanProtocol.Show(plan, tab.Path));
			} else {
				State.Set("editor", tab.Path, "agentPlanRemoved", WireJson.Default.AgentPlanRemoved, new(tab.Path));
			}
		}
		State.Set("planReview", "reviews", "reviews", WireJson.Default.AgentPlanReviewsWire, new(plans.Reviews));
		_openedPlanReviews.IntersectWith(plans.Reviews.Select(review => review.RequestId));
		if (!autoOpen) return;
		foreach (var review in plans.Reviews) {
			if (!_openedPlanReviews.Add(review.RequestId)) continue;
			if (review.Editable) PublishEditorFileOpen(review.Path, null, preview: false, scratch: false, EditorOpenIntent.Background);
			else OpenAgentPlan(documents[review.Path], review.Path, EditorOpenIntent.Background);
		}
	}
}
