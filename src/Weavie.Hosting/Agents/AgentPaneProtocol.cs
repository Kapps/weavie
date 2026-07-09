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
			message = new {
				type = message.Type,
				providerId = message.ProviderId,
				threadId = message.ThreadId,
				turnId = message.TurnId,
				itemId = message.ItemId,
				itemType = message.ItemType,
				summary = message.Summary,
				text = message.Text,
				status = message.Status,
				payload = ParsePayload(message.PayloadJson),
			},
		});
	}

	private static JsonElement? ParsePayload(string? json) =>
		string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
