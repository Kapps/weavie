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
		if (_loadingTranscript && kind is not ("available_commands_update" or "current_mode_update"
			or "config_option_update" or "usage_update")) return;
		switch (kind) {
			case "user_message_chunk": break;
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

		if (!AcpContentAnnotations.IsUserVisible(content)) return;
		string turnId = TurnId();
		string id = $"{itemType}:{OptionalString(update, "messageId") ?? turnId}";
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
		Emit(message);
	}

	private void CompleteContentStreams() {
		foreach (var message in DrainContentStreams()) Emit(message);
	}

	private IReadOnlyList<AgentPaneMessage> DrainContentStreams() {
		AcpContentState[] content;
		lock (_gate) {
			content = [.. _content.Values];
			_content.Clear();
		}
		return [.. content.Select(state => new AgentPaneMessage {
				Type = "item-completed",
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
			})];
	}

	private static string? ResourceText(JsonElement content) {
		if (!content.TryGetProperty("resource", out var resource)) {
			return OptionalString(content, "description") ?? OptionalString(content, "title");
		}
		return OptionalString(resource, "text");
	}

	private static string? EmbeddedUri(JsonElement content) =>
		content.TryGetProperty("resource", out var resource) ? OptionalString(resource, "uri") : null;

}
