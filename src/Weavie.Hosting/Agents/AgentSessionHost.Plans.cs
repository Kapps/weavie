using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal event Action<IReadOnlyList<AgentPlan>>? PlanDocumentsChanged;

	internal void WithPlanDocuments(Action<IReadOnlyList<AgentPlan>> publish) {
		ArgumentNullException.ThrowIfNull(publish);
		lock (_paneGate) publish(PlanDocumentsLocked());
	}

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

	private AgentPlan? CompletedPlanLocked(AgentPaneMessage message) {
		if (message.Type != "item-completed" || message.ItemType != "plan"
			|| string.IsNullOrWhiteSpace(message.Text)
			|| AgentPaneIdentity.ItemKey(message) is not { } key
			|| _paneActiveItems.Contains(key)) return null;
		return new AgentPlan(key, "Plan", message.Text);
	}

	private IReadOnlyList<AgentPlan> PlanDocumentsLocked() =>
		[.. _paneMessages.Select(CompletedPlanLocked).OfType<AgentPlan>()];

	private void PublishPlanDocumentsLocked() => PlanDocumentsChanged?.Invoke(PlanDocumentsLocked());
}
