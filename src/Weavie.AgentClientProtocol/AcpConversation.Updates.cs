using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	internal void HandleNotification(JsonElement root) {
		lock (_turnTransitionGate) {
			if (Live) HandleNotificationSerialized(root);
			else if (_endpoint.IsSet) _endpoint.Value.Drop(root);
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
		if (sessionId != _endpoint.Value.SessionId) throw new AcpProtocolException("ACP update targets another conversation.");
		string kind = RequiredString(update, "sessionUpdate", "session/update notification");
		if (_loadingTranscript) {
			_replay.Observe(kind, update);
			ObserveReplayedBackground(kind, update);
			if (kind is not ("available_commands_update" or "current_mode_update" or "config_option_update" or "usage_update")) return;
		}
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
			case "notice": EmitNotice(update); break;
			case "subagent_spawned" or "subagent_state_update" or "async_task_spawned" or "async_task_progress" or "async_task_state_update":
				HandleBackgroundUpdate(kind, update);
				break;
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

}
