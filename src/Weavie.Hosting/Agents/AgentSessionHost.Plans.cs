using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal event Action<IReadOnlyList<AgentPlan>>? PlanDocumentsChanged;

	internal bool WithCompletedPlan(string threadId, string turnId, string itemId, Action<AgentPlan> publish) {
		ArgumentNullException.ThrowIfNull(publish);
		if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(itemId)) return false;
		string key = AgentPaneIdentity.ItemKey(threadId, turnId, itemId)!;
		lock (_paneGate) {
			if (!_pane.TryItemIndex(key, out int index)
				|| CompletedPlanLocked(_pane.Stored[index]) is not { } plan) return false;
			publish(plan);
			return true;
		}
	}

	private AgentPlan? CompletedPlanLocked(AgentPaneMessage message) {
		if (message.Type != "item-completed" || message.ItemType != "plan"
			|| string.IsNullOrWhiteSpace(message.Text)
			|| AgentPaneIdentity.ItemKey(message) is not { } key
			|| _pane.IsActive(key)) return null;
		return new AgentPlan(key, "Plan", message.Text);
	}

	private void PublishPlanDocumentsLocked() => PlanDocumentsChanged?.Invoke(
		[.. _pane.Stored.Select(CompletedPlanLocked).OfType<AgentPlan>()]);
}
