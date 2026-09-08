using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void BufferReplayedUserMessage(JsonElement update, JsonElement content) {
		lock (_gate) {
			if (!_loadingTranscript) return;
		}
		string? messageId = OptionalString(update, "messageId");
		if (_replayedUserMessage is { } pending && pending.MessageId != messageId) {
			CloseReplayedUserMessage();
		}
		if (!AcpContentAnnotations.IsUserVisible(content)) return;
		_replayedUserMessage ??= new(messageId, []);
		_replayedUserMessage.Blocks.Add(content.Clone());
	}

	private void CloseReplayedUserMessage() {
		if (_replayedUserMessage is not { } pending) return;
		_replayedUserMessage = null;
		string text = string.Concat(pending.Blocks.Select(block =>
			OptionalString(block, "type") == "text" ? OptionalString(block, "text") : ResourceText(block)));
		var media = pending.Blocks.FirstOrDefault(block => OptionalString(block, "type") is "image" or "audio");
		string? uri = pending.Blocks.Select(block => OptionalString(block, "uri") ?? EmbeddedUri(block))
			.FirstOrDefault(value => value is not null);
		if (text.Length == 0 && media.ValueKind == JsonValueKind.Undefined && uri is null) return;
		string turnId = TurnIdForUpdate(userMessage: true);
		PublishPane(new AgentPaneMessage {
			Type = "user-message",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			TurnId = turnId,
			ItemId = $"userMessage:{pending.MessageId ?? turnId}",
			ItemType = "userMessage",
			Text = text.Length == 0 ? null : text,
			Status = "completed",
			MediaType = media.ValueKind == JsonValueKind.Undefined ? null : OptionalString(media, "mimeType"),
			MediaData = media.ValueKind == JsonValueKind.Undefined ? null : OptionalString(media, "data"),
			ResourceUri = uri,
		});
	}

	private sealed record ReplayedUserMessage(string? MessageId, List<JsonElement> Blocks);
}
