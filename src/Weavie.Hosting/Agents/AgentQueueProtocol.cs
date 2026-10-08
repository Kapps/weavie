using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Builds the web-facing view of the submissions an agent accepted but has not delivered yet.</summary>
internal static class AgentQueueProtocol {
	public static AgentQueueMessage Message(IReadOnlyList<AgentTurnSubmission> queued) {
		ArgumentNullException.ThrowIfNull(queued);
		return new([.. queued.Select(submission => new AgentQueuedWire(submission.Text, submission.Attachments.Count))]);
	}
}

internal sealed record AgentQueueMessage(IReadOnlyList<AgentQueuedWire> Queued);

internal sealed record AgentQueuedWire(string Text, int Attachments);
