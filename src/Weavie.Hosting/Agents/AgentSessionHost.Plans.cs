using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal event Action<AgentPlansSnapshot>? PlansChanged;

	internal bool WithCompletedPlan(string threadId, string turnId, string itemId, Action<AgentPlan> publish) {
		ArgumentNullException.ThrowIfNull(publish);
		if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(itemId)) return false;
		string key = AgentPaneIdentity.ItemKey(threadId, turnId, itemId)!;
		lock (_paneGate) {
			if (!_paneItemIndexes.TryGetValue(key, out int index)
				|| CompletedPlanLocked(_paneMessages[index]) is not { } plan) return false;
			publish(plan);
			return true;
		}
	}

	// A text plan review is a document of its own; a file review's document is the file.
	private AgentPlan? CompletedPlanLocked(AgentPaneMessage message) {
		bool document = message is { Type: "item-completed", ItemType: "plan" }
			or { Type: "approval-requested", ItemType: "plan-review", PlanPath: null };
		if (!document || string.IsNullOrWhiteSpace(message.Text)
			|| AgentPaneIdentity.ItemKey(message) is not { } key
			|| _paneActiveItems.Contains(key)) return null;
		return new AgentPlan(key, "Plan", message.Text);
	}

	// Only plan items and the resolution of a plan review change the projection.
	private bool AffectsPlansLocked(AgentPaneMessage message) =>
		message.ItemType is "plan" or "plan-review" || message.Type == "item-retracted"
		|| message.Type == "approval-resolved" && AgentPaneIdentity.ItemKey(message) is { } key
			&& _paneItemIndexes.TryGetValue(key, out int index) && _paneMessages[index].ItemType == "plan-review";

	private void PublishPlansLocked() {
		var documents = new List<AgentPlan>();
		var reviews = new Dictionary<string, AgentPlanReview>(StringComparer.Ordinal);
		foreach (var message in _paneMessages) {
			var plan = CompletedPlanLocked(message);
			if (plan is { } document) documents.Add(document);
			if (AgentPaneIdentity.ItemKey(message) is not { } key) continue;
			if (message is { Type: "approval-requested", ItemType: "plan-review" }) {
				reviews[key] = new AgentPlanReview(
					message.RequestId!,
					message.PlanPath ?? AgentPlanProtocol.Path(plan!.Value),
					message.PlanPath is not null,
					message.Actions ?? []);
			} else if (message.Type == "approval-resolved") {
				reviews.Remove(key);
			}
		}
		PlansChanged?.Invoke(new(documents, [.. reviews.Values]));
	}
}

internal sealed record AgentPlansSnapshot(IReadOnlyList<AgentPlan> Documents, IReadOnlyList<AgentPlanReview> Reviews);

/// <summary>A pending plan approval, answered from the editor tab at <paramref name="Path"/>.</summary>
internal sealed record AgentPlanReview(string RequestId, string Path, bool Editable, IReadOnlyList<AgentActionOption> Actions);
