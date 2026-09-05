using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Builds the web-facing view of the submissions an agent accepted but has not delivered yet.</summary>
internal static class AgentQueueProtocol {
	public static object Message(IReadOnlyList<AgentTurnSubmission> queued) {
		ArgumentNullException.ThrowIfNull(queued);
		return new {
			queued = queued.Select(submission => new {
				text = submission.Text,
				attachments = submission.Attachments.Count,
			}),
		};
	}
}
