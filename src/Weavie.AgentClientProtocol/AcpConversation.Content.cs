using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private ContentRun? _contentRun;
	// The last agent message completed since the agent last stopped.
	private ContentRun? _reply;

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
		_contentRun ??= new ContentRun($"content:{Guid.NewGuid():N}", _endpoint.Value.SessionId, turnId, messageId, itemType) {
			Held = itemType == "agentMessage" ? "" : null,
		};
		// Codex's adapter tags the message it ends a turn's work on.
		_contentRun.Final |= Air(update, "phase") is { ValueKind: JsonValueKind.String } phase && phase.GetString() == "final_answer";
		// A reply is held back while it can still turn out to be the no-reply message, which is never shown.
		if (_contentRun.Held is { } held) {
			if (type == "text" && AgentNoReply.CouldBecome(held + text)) {
				_contentRun.Held = held + text;
				return;
			}
			_contentRun.Held = null;
			if (type == "text") text = held + text;
			else if (held.Length > 0) Emit(ContentMessage(_contentRun, deltaType) with { Text = held });
		}
		Emit(ContentMessage(_contentRun, deltaType) with {
			Text = text,
			MediaType = type is "image" or "audio" ? OptionalString(content, "mimeType") : null,
			MediaData = type is "image" or "audio" ? OptionalString(content, "data") : null,
			ResourceUri = OptionalString(content, "uri") ?? EmbeddedUri(content),
		});
	}

	private void CompleteContentStreams() {
		foreach (var message in DrainContentStreams()) Emit(message);
	}

	/// <summary>Completes open content at a point where the agent stopped, marking the message it stopped on.</summary>
	private void CompleteReply() {
		if (_contentRun is { ItemType: "agentMessage" } open) open.Final = true;
		CompleteContentStreams();
		if (_reply is { Final: false } run && _port.CompleteContent(run.ThreadId, run.TurnId, run.ItemId) is { } row) {
			Emit(row with { Final = true });
		}
		_reply = null;
	}

	private IReadOnlyList<AgentPaneMessage> DrainContentStreams() {
		var run = _contentRun;
		_contentRun = null;
		if (run is null) return [];
		// The agent still has a no-reply message, so its id is kept, on a row nothing draws, for Rewind to fork at.
		if (run.Held is { } held && AgentNoReply.Is(held)) {
			_reply = null;
			return run.MessageId is null ? [] : [ContentMessage(run, "item-retracted") with { Status = "retracted" }];
		}
		var completed = run.Held is { } text
			? ContentMessage(run, "item-completed") with { Text = text, Status = "completed" }
			: _port.CompleteContent(run.ThreadId, run.TurnId, run.ItemId);
		if (completed is null) return [];
		if (run.ItemType == "agentMessage") _reply = run;
		return [completed with { Final = run.Final ? true : null }];
	}

	private AgentPaneMessage ContentMessage(ContentRun run, string type) => new() {
		Type = type,
		ProviderId = Definition.Id,
		ThreadId = run.ThreadId,
		TurnId = run.TurnId,
		MessageId = run.MessageId,
		ItemId = run.ItemId,
		ItemType = run.ItemType,
	};

	private static string? ResourceText(JsonElement content) {
		if (!content.TryGetProperty("resource", out var resource)) {
			return OptionalString(content, "description") ?? OptionalString(content, "title");
		}
		return OptionalString(resource, "text");
	}

	private static string? EmbeddedUri(JsonElement content) =>
		content.TryGetProperty("resource", out var resource) ? OptionalString(resource, "uri") : null;

	private sealed record ContentRun(string ItemId, string? ThreadId, string TurnId, string? MessageId, string ItemType) {
		// The run's text so far while nothing of it has been emitted; null once it has.
		public string? Held { get; set; }

		public bool Final { get; set; }
	}
}
