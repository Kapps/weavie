using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Serializes provider-neutral native agent pane messages for the web bridge.</summary>
internal static class AgentPaneProtocol {
	public static string Reset(string slot, string workspace) {
		ArgumentNullException.ThrowIfNull(slot);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		return JsonSerializer.Serialize(new {
			type = "agent-pane-reset",
			slot,
			workspace,
		});
	}

	public static string Message(string slot, string workspace, AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(slot);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(message);
		return JsonSerializer.Serialize(new {
			type = "agent-pane",
			slot,
			workspace,
			message = Body(message),
		});
	}

	/// <summary>
	/// One frame carrying an entire pane snapshot, so a reconnect's replay is a single bridge message instead of
	/// one per transcript entry — a long transcript would otherwise burst past the bridge's bounded outbox and get
	/// the (healthy, network-slow) page dropped. The web applies each message in order, same as a live stream.
	/// </summary>
	public static string Batch(string slot, string workspace, IReadOnlyList<AgentPaneMessage> messages) {
		ArgumentNullException.ThrowIfNull(slot);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(messages);
		return JsonSerializer.Serialize(new {
			type = "agent-pane-batch",
			slot,
			workspace,
			messages = messages.Select(Body),
		});
	}

	private static object Body(AgentPaneMessage message) => new {
		type = message.Type,
		providerId = message.ProviderId,
		threadId = message.ThreadId,
		isPrimaryThread = message.IsPrimaryThread,
		turnId = message.TurnId,
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
		payload = ParsePayload(message.PayloadJson),
	};

	private static JsonElement? ParsePayload(string? json) =>
		string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
