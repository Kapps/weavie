using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	private readonly Lock _planGate = new();
	private AgentPlansSnapshot? _publishedPlans;
	internal event Action<AgentPlansSnapshot>? PlansChanged;

	internal bool WithCompletedPlan(string providerId, string? conversationId, string threadId, string turnId, string itemId, Action<AgentPlan> publish) {
		lock (_planGate) {
			if (Message(providerId, conversationId, threadId, turnId, itemId) is not { } message || CompletedPlan(message) is not { } plan) return false;
			publish(plan);
			return true;
		}
	}

	internal bool WithPlanFile(string providerId, string? conversationId, string threadId, string turnId, string itemId, Action<string> open) {
		lock (_planGate) {
			if (Message(providerId, conversationId, threadId, turnId, itemId) is not { ItemType: "plan-review", PlanPath: { } path }) return false;
			open(path);
			return true;
		}
	}

	private AgentPaneMessage? Message(string providerId, string? conversationId, string threadId, string turnId, string itemId) =>
		string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(itemId)
			? null : Output.FindItem(AgentPaneIdentity.ItemKey(providerId, conversationId, threadId, turnId, itemId)!);

	private static AgentPlan? CompletedPlan(AgentPaneMessage message) =>
		message is { Type: "item-completed", ItemType: "plan" }
			&& !string.IsNullOrWhiteSpace(message.Text) && AgentPaneIdentity.ItemKey(message) is { } key
			? new AgentPlan(key, "Plan", message.Text) : null;

	private void PublishPlans() {
		lock (_planGate) {
			var documents = new List<AgentPlan>();
			var reviews = new Dictionary<string, AgentPlanReview>(StringComparer.Ordinal);
			foreach (var record in Output.ReadHistory(null, null).Messages) {
				var message = record.Message;
				if (CompletedPlan(message) is { } document) documents.Add(document);
				if (AgentPaneIdentity.ItemKey(message) is not { } key) continue;
				if (message is { Type: "approval-requested", ItemType: "plan-review", PlanPath: { } path }) {
					reviews[key] = new AgentPlanReview(message.RequestId!, path, message.Actions ?? []);
				} else if (message.Type == "approval-resolved") {
					reviews.Remove(key);
				}
			}
			var next = new AgentPlansSnapshot(documents, [.. reviews.Values]);
			if (_publishedPlans is { } previous && previous.Documents.SequenceEqual(next.Documents)
				&& previous.Reviews.SequenceEqual(next.Reviews)) return;
			_publishedPlans = next;
			PlansChanged?.Invoke(next);
		}
	}
}

internal sealed record AgentPlansSnapshot(IReadOnlyList<AgentPlan> Documents, IReadOnlyList<AgentPlanReview> Reviews);

/// <summary>A pending plan approval, answered from the editor tab of its plan file <paramref name="Path"/>.</summary>
internal sealed record AgentPlanReview(string RequestId, string Path, IReadOnlyList<AgentActionOption> Actions);
