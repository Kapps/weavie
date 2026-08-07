using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Builds provider-neutral native agent pane payloads.</summary>
internal static class AgentPaneProtocol {
	public static object Message(AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		return Body(message);
	}

	/// <summary>
	/// One payload carrying an entire pane snapshot, so a reconnect's replay is a single message instead of
	/// one per transcript entry — a long transcript would otherwise burst past the bridge's bounded outbox and get
	/// the (healthy, network-slow) page dropped. The web applies each message in order, same as a live stream.
	/// </summary>
	public static object Batch(IReadOnlyList<AgentPaneMessage> messages) {
		ArgumentNullException.ThrowIfNull(messages);
		return new { messages = messages.Select(Body) };
	}

	private static object Body(AgentPaneMessage message) => new {
		type = message.Type,
		providerId = message.ProviderId,
		threadId = message.ThreadId,
		isPrimaryThread = message.IsPrimaryThread,
		turnId = message.TurnId,
		startedAtMs = message.StartedAtMs,
		itemId = message.ItemId,
		itemType = message.ItemType,
		category = message.Category,
		summary = message.Summary,
		text = message.Text,
		status = message.Status,
		questions = message.Questions?.Select(question => new {
			id = question.Id,
			header = question.Header,
			question = question.Question,
			isSecret = question.IsSecret,
			options = question.Options.Select(option => new {
				label = option.Label,
				description = option.Description,
			}),
		}),
		payload = string.IsNullOrWhiteSpace(message.PayloadJson)
			? (System.Text.Json.JsonElement?)null
			: System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message.PayloadJson),
	};
}
