using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private ContentRun? _contentRun;

	private void EmitContent(JsonElement update, string deltaType, string itemType) {
		if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("An ACP content update is missing its content block.");
		}
		if (!AcpContentAnnotations.IsUserVisible(content)) return;
		string? type = OptionalString(content, "type");
		string? text = type == "text" ? OptionalString(content, "text") : ResourceText(content);
		if (type == "text" && string.IsNullOrEmpty(text)) return;
		string turnId = TurnId();
		string? messageId = OptionalString(update, "messageId");
		if (_contentRun is { } previous
			&& (previous.TurnId != turnId || previous.ItemType != itemType || previous.MessageId != messageId)) {
			CompleteContentStreams();
		}
		_contentRun ??= new ContentRun($"content:{Guid.NewGuid():N}", _endpoint.Value.SessionId, turnId, messageId, itemType);
		Emit(new AgentPaneMessage {
			Type = deltaType,
			ProviderId = Definition.Id,
			ThreadId = _contentRun.ThreadId,
			TurnId = turnId,
			MessageId = messageId,
			ItemId = _contentRun.ItemId,
			ItemType = itemType,
			Text = text,
			MediaType = type is "image" or "audio" ? OptionalString(content, "mimeType") : null,
			MediaData = type is "image" or "audio" ? OptionalString(content, "data") : null,
			ResourceUri = OptionalString(content, "uri") ?? EmbeddedUri(content),
		});
	}

	private void CompleteContentStreams() {
		foreach (var message in DrainContentStreams()) Emit(message);
	}

	private IReadOnlyList<AgentPaneMessage> DrainContentStreams() {
		var run = _contentRun;
		_contentRun = null;
		return run is not null && _port.CompleteContent(run.ThreadId, run.TurnId, run.ItemId) is { } message
			? [message] : [];
	}

	private static string? ResourceText(JsonElement content) {
		if (!content.TryGetProperty("resource", out var resource)) {
			return OptionalString(content, "description") ?? OptionalString(content, "title");
		}
		return OptionalString(resource, "text");
	}

	private static string? EmbeddedUri(JsonElement content) =>
		content.TryGetProperty("resource", out var resource) ? OptionalString(resource, "uri") : null;

	private sealed record ContentRun(string ItemId, string? ThreadId, string TurnId, string? MessageId, string ItemType);
}
