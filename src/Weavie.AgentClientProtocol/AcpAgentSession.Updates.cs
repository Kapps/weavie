using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void HandleNotification(long generation, JsonElement root) {
		lock (_turnTransitionGate) {
			if (!OwnsGeneration(generation)) return;
			HandleNotificationSerialized(root);
		}
	}

	private void HandleNotificationSerialized(JsonElement root) {
		string method = OptionalString(root, "method") ?? string.Empty;
		if (method == "$/cancel_request") {
			CancelClientRequest(root);
			return;
		}
		if (method == "elicitation/complete") {
			if (!root.TryGetProperty("params", out var completion)
				|| completion.ValueKind != JsonValueKind.Object) {
				throw new AcpProtocolException("ACP elicitation completion is missing its parameters.");
			}
			CompleteElicitation(completion);
			return;
		}
		if (method != "session/update") return;
		if (!root.TryGetProperty("params", out var parameters)
			|| parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("update", out var update)
			|| update.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("ACP session/update is missing its parameters or update object.");
		}
		string sessionId = OptionalString(parameters, "sessionId")
			?? throw new AcpProtocolException("An ACP session/update notification is missing sessionId.");
		if (sessionId != Endpoint(_activeGeneration).SessionId) throw new AcpProtocolException("ACP update targets another conversation.");
		string kind = RequiredString(update, "sessionUpdate", "session/update notification");
		if (kind != "user_message_chunk") CloseReplayedUserMessage(null);
		switch (kind) {
			case "user_message_chunk": EmitContent(update, "user-message-delta", "userMessage"); break;
			case "agent_message_chunk": EmitContent(update, "agent-message-delta", "agentMessage"); break;
			case "agent_thought_chunk": EmitContent(update, "thought-message-delta", "thought"); break;
			case "tool_call": UpdateTool(update, initial: true); break;
			case "tool_call_update": UpdateTool(update, initial: false); break;
			case "plan": EmitProgress(update); break;
			case "plan_update": UpdatePlan(update); break;
			case "plan_removed": RemovePlan(update); break;
			case "available_commands_update": UpdateCommands(update); break;
			case "current_mode_update": UpdateMode(update); break;
			case "config_option_update": UpdateConfig(update); break;
			case "session_info_update": EmitSessionInfo(update); break;
			case "usage_update": EmitUsage(update); break;
			default: throw new AcpProtocolException($"Unsupported ACP session update '{kind}'.");
		}
	}

	private void CancelClientRequest(JsonElement root) {
		if (!root.TryGetProperty("params", out var parameters)
			|| !parameters.TryGetProperty("requestId", out var requestId)
			|| requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) {
			throw new AcpProtocolException("ACP request cancellation requires a string or numeric requestId.");
		}
		string id = AcpJsonRpcConnection.CanonicalId(requestId);
		if (!_clientRequests.TryGetValue(id, out var state) || !state.TryCancel()) return;
		CancelCompletedClientRequest(state);
	}

	private void EmitContent(JsonElement update, string deltaType, string itemType) {
		if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("An ACP content update is missing its content block.");
		}
		if (itemType == "userMessage") {
			lock (_gate) {
				if (!_loadingTranscript) return;
			}
			if (IsInjectedContext(content)) return;
		}
		string? advertisedId = OptionalString(update, "messageId");
		string? advertisedKey = advertisedId is null ? null : $"{itemType}:{advertisedId}";
		string turnId = TurnIdForContent(itemType, advertisedKey);
		string id = advertisedKey ?? $"{itemType}:{turnId}";
		if (itemType == "userMessage") CloseReplayedUserMessage(id);
		string? type = OptionalString(content, "type");
		string? text = type == "text" ? OptionalString(content, "text") : ResourceText(content);
		AcpContentState state;
		lock (_gate) {
			if (!_content.TryGetValue(id, out state!)) {
				state = new AcpContentState { Id = id, ItemType = itemType, TurnId = turnId };
				_content.Add(id, state);
			}
			state.Text.Append(text);
			state.MediaType ??= type is "image" or "audio" ? OptionalString(content, "mimeType") : null;
			state.MediaData ??= type is "image" or "audio" ? OptionalString(content, "data") : null;
			state.ResourceUri ??= OptionalString(content, "uri") ?? EmbeddedUri(content);
		}
		if (itemType == "userMessage") {
			return;
		}
		var message = new AgentPaneMessage {
			Type = deltaType,
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			TurnId = state.TurnId,
			ItemId = id,
			ItemType = itemType,
			Text = text,
			MediaType = type is "image" or "audio" ? OptionalString(content, "mimeType") : null,
			MediaData = type is "image" or "audio" ? OptionalString(content, "data") : null,
			ResourceUri = OptionalString(content, "uri") ?? EmbeddedUri(content),
		};
		PublishPane(message);
	}

	// A replayed user prompt has no local submission to place it and no streamed delta holding its position, so it
	// exists only once its stream ends. Close it as soon as the replay moves past it, or every prompt lands at the
	// end of the load -- after the responses it asked for, and with the turn boundaries the pane derives from it.
	private void CloseReplayedUserMessage(string? keepId) {
		lock (_gate) {
			if (!_loadingTranscript) return;
		}
		CompleteContentStreams(state => state.ItemType == "userMessage"
			&& !string.Equals(state.Id, keepId, StringComparison.Ordinal));
	}

	private void CompleteContentStreams() => CompleteContentStreams(static _ => true);

	private void CompleteContentStreams(Func<AcpContentState, bool> match) {
		AcpContentState[] content;
		lock (_gate) {
			content = [.. _content.Values.Where(match)];
			foreach (var state in content) _content.Remove(state.Id);
		}
		foreach (var state in content) {
			PublishPane(new AgentPaneMessage {
				Type = state.ItemType == "userMessage" ? "user-message" : "item-completed",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = state.TurnId,
				ItemId = state.Id,
				ItemType = state.ItemType,
				Category = state.ItemType is "thought" or "plan" ? state.ItemType : null,
				Summary = state.ItemType switch {
					"thought" => "Reasoning",
					"plan" => "Plan",
					_ => null,
				},
				Text = state.Text.Length == 0 ? null : state.Text.ToString(),
				Status = "completed",
				MediaType = state.MediaType,
				MediaData = state.MediaData,
				ResourceUri = state.ResourceUri,
			});
		}
	}

	private string TurnIdForUpdate(bool userMessage) {
		lock (_gate) {
			if (!_loadingTranscript) {
				return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}

			if (userMessage) _turnNumber++;
			else if (_turnNumber == 0) _turnNumber = 1;
			_replayContentRole = userMessage ? "userMessage" : "other";

			return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	private string TurnIdForContent(string itemType, string? messageId) {
		lock (_gate) {
			if (!_loadingTranscript) {
				return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}

			if (itemType == "userMessage") {
				bool newMessage = messageId is null
					? _replayContentRole != "userMessage"
					: !_content.ContainsKey(messageId);
				if (newMessage) _turnNumber++;
			} else if (_turnNumber == 0) {
				_turnNumber = 1;
			}
			_replayContentRole = itemType;
			return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	private static string? ResourceText(JsonElement content) {
		if (!content.TryGetProperty("resource", out var resource)) {
			return OptionalString(content, "description") ?? OptionalString(content, "title");
		}
		return OptionalString(resource, "text");
	}

	private static string? EmbeddedUri(JsonElement content) =>
		content.TryGetProperty("resource", out var resource) ? OptionalString(resource, "uri") : null;

	private static bool IsInjectedContext(JsonElement content) =>
		EmbeddedUri(content) is { } uri
		&& (string.Equals(uri, "weavie://instructions", StringComparison.Ordinal)
			|| uri.EndsWith("#selection", StringComparison.Ordinal));

}
