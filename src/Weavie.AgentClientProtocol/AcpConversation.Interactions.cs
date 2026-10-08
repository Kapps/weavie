using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	internal void ResolvePermission(string requestId, string optionId) {
		if (!_pendingRequests.TryGetValue(requestId, out var pending) || pending.Kind != "permission") {
			EmitStaleInteraction(requestId, "permission");
			return;
		}
		var option = pending.Data.EnumerateArray().FirstOrDefault(candidate =>
			string.Equals(OptionalString(candidate, "optionId"), optionId, StringComparison.Ordinal));
		if (option.ValueKind == System.Text.Json.JsonValueKind.Undefined) {
			EmitFailure(new AcpProtocolException($"'{optionId}' was not advertised for permission {requestId}."));
			return;
		}
		if (!CompleteDeferredClientRequest(
			requestId,
			AcpContent.Selected(optionId))) {
			EmitStaleInteraction(requestId, "permission");
			return;
		}
		ResolveInteraction(
			requestId,
			"approval-resolved",
			PermissionStatus(option),
			permission: true,
			pending.ThreadId,
			pending.TurnId,
			answers: null);
		if (OptionalString(option, "kind") is "reject_once" or "reject_always") CompletePermissionTool(pending.Request);
	}

	internal void ResolveInput(
		string requestId,
		string action,
		IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		if (action is not ("accept" or "decline" or "cancel")) {
			EmitFailure(new AcpProtocolException($"Unsupported ACP elicitation action '{action}'."));
			return;
		}
		if (!_pendingRequests.TryGetValue(requestId, out var pending) || pending.Kind is not ("input" or "url")) {
			EmitStaleInteraction(requestId, "input");
			return;
		}
		JsonObject? content = null;
		try {
			if (action == "accept") content = pending.Kind == "input"
				? AcpElicitationSchema.BuildElicitationContent(pending.Data, answers)
				: [];
		} catch (AcpProtocolException ex) {
			EmitFailure(ex);
			return;
		}
		var response = new JsonObject { ["action"] = action };
		if (action == "accept") response["content"] = content;
		if (!CompleteDeferredClientRequest(requestId, response)) {
			EmitStaleInteraction(requestId, "input");
			return;
		}
		ResolveInteraction(
			requestId,
			"input-resolved",
			action == "accept" ? "accepted" : action,
			permission: false,
			pending.ThreadId,
			pending.TurnId,
			action == "accept" ? answers : null);
	}

	private void CompleteElicitation(System.Text.Json.JsonElement parameters) =>
		RequiredString(parameters, "elicitationId", "elicitation completion");

	private bool CancelPendingInteractions() {
		bool cancelled = false;
		foreach (var entry in _pendingRequests) {
			if (!_pendingRequests.TryRemove(entry.Key, out var pending)) continue;
			cancelled = true;
			CompletePermissionTool(pending.Request);
			try {
				if (pending.Kind == "permission") {
					CompleteDeferredClientRequest(
						entry.Key,
						AcpContent.Cancelled());
				} else {
					CompleteDeferredClientRequest(entry.Key, new JsonObject { ["action"] = "cancel" });
				}
			} catch (Exception ex) when (ex is InvalidOperationException or IOException) {
				// The process generation that owned the request is already gone.
			}
			ResolveInteraction(
				entry.Key,
				pending.Kind == "permission" ? "approval-resolved" : "input-resolved",
				"cancelled",
				pending.Kind == "permission",
				pending.ThreadId,
				pending.TurnId,
				answers: null);
		}
		bool cancelAuthentication;
		bool requiresUserInput;
		string? authenticationItemId;
		CancellationTokenSource? authenticationCancellation;
		lock (_gate) {
			cancelAuthentication = _authenticationPending;
			_authenticationPending = false;
			_authenticating = false;
			_authenticationOpensSession = false;
			authenticationItemId = _authenticationItemId;
			_authenticationItemId = null;
			authenticationCancellation = _authenticationCancellation;
			_authenticationCancellation = null;
			if (cancelAuthentication && authenticationItemId is not null) {
				_resolvedRequests.Add(authenticationItemId);
			}
			requiresUserInput = HasPendingInteractionLocked();
			authenticationCancellation?.Cancel();
		}
		if (!cancelAuthentication) return cancelled;
		Observe(new AgentInputResolved(requiresUserInput));
		Emit(new AgentPaneMessage {
			Type = "authentication-resolved",
			ProviderId = Definition.Id,
			ThreadId = SessionId(),
			ItemId = authenticationItemId,
			RequestId = authenticationItemId,
			Status = "cancelled",
		});
		return true;
	}

	private void ResolveInteraction(
		string requestId,
		string type,
		string status,
		bool permission,
		string? threadId,
		string turnId,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? answers) {
		bool requiresUserInput;
		lock (_gate) {
			_resolvedRequests.Add(requestId);
			requiresUserInput = HasPendingInteractionLocked();
		}
		if (permission) Observe(new AgentPermissionResolved(requiresUserInput));
		else Observe(new AgentInputResolved(requiresUserInput));
		Emit(new AgentPaneMessage {
			Type = type,
			ProviderId = Definition.Id,
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = $"request:{requestId}",
			RequestId = requestId,
			Status = status,
			Answers = answers,
		});
	}

	private void EmitStaleInteraction(string requestId, string kind) {
		lock (_gate) {
			if (_resolvedRequests.Contains(requestId)) return;
		}
		EmitFailure(new AcpProtocolException($"ACP {kind} request '{requestId}' is no longer pending."));
	}

	private static string PermissionStatus(System.Text.Json.JsonElement option) => OptionalString(option, "kind") switch {
		"allow_once" => "allowed once",
		"allow_always" => "always allowed",
		"reject_once" or "reject_always" => "denied",
		_ => OptionalString(option, "name") ?? RequiredString(option, "optionId", "permission option"),
	};

	private bool HasPendingInteractionLocked() => !_pendingRequests.IsEmpty || _authenticationPending;

	private bool CompleteDeferredClientRequest(string requestId, JsonObject response) {
		if (!_clientRequests.TryGetValue(requestId, out var state) || !state.TryComplete()) return false;
		_pendingRequests.TryRemove(requestId, out _);
		RespondToCompletedClientRequest(state, response, errorCode: null, errorMessage: null, errorData: null);
		return true;
	}
}
