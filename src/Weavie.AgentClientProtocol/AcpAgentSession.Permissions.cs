using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void HandlePermissionRequest(AcpClientRequestState state) {
		try {
			ValidateRequestSession(state.Request);
			CompleteClientResponse(state, RequestPermission(state.Request, state));
		} catch (Exception ex) {
			HandleClientRequestFailure(state, ex);
		}
	}

	private object RequestPermission(AcpClientRequest request, AcpClientRequestState state) {
		if (!request.Parameters.TryGetProperty("toolCall", out var update) || update.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("An ACP permission request is missing toolCall.");
		}
		if (!request.Parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("An ACP permission request is missing options.");
		}
		var actions = options.EnumerateArray().Select(option => new AgentActionOption {
			Id = RequiredString(option, "optionId", "permission option"),
			Label = RequiredString(option, "name", "permission option"),
			Kind = RequiredString(option, "kind", "permission option"),
		}).ToArray();
		var tool = MergeTool(update, ToolUpdateSource.Permission);
		if (Mutation(tool) is not AgentMutation.None) EnsureObservedMutation(tool);
		if (tool.Kind != "switch_mode" && _context.Settings.RequireBool(AgentSettings.AllowAllPermissions)) {
			string? optionId = options.EnumerateArray()
				.Where(option => OptionalString(option, "kind") is "allow_always" or "allow_once")
				.OrderBy(option => OptionalString(option, "kind") == "allow_always" ? 0 : 1)
				.Select(option => OptionalString(option, "optionId"))
				.FirstOrDefault(id => id is not null);
			string selected = optionId ?? throw new AcpProtocolException(
				"Permission bypass is enabled, but ACP advertised no allow option.");
			return new { outcome = new { outcome = "selected", optionId = selected } };
		}

		string? threadId = SessionId();
		string turnId = tool.TurnId;
		if (!_pendingRequests.TryAdd(
			request.Id,
			new AcpPendingRequest(request, "permission", options.Clone(), threadId, turnId))) {
			throw new AcpProtocolException($"ACP request id '{request.Id}' is already pending.");
		}
		if (!state.PublishDeferred(() => {
			Observe(new AgentPermissionRequested());
			Observe(new AgentPermissionResolved(RequiresUserInput: true));
			Emit(new AgentPaneMessage {
				Type = "approval-requested",
				ProviderId = _definition.Id,
				ThreadId = threadId,
				TurnId = turnId,
				ItemId = $"request:{request.Id}",
				RequestId = request.Id,
				ItemType = tool.Kind ?? "tool",
				Category = tool.Kind,
				Summary = tool.Title ?? "Permission requested",
				Text = tool.Input,
				Actions = actions,
				Status = "pending",
			});
		})) {
			_pendingRequests.TryRemove(request.Id, out _);
			state.Token.ThrowIfCancellationRequested();
		}
		return DeferredClientResponse;
	}

	private static string? ToolRequestText(JsonElement tool) {
		if (tool.TryGetProperty("rawInput", out var rawInput)) {
			return rawInput.ValueKind == JsonValueKind.String ? rawInput.GetString() : rawInput.GetRawText();
		}
		return null;
	}

	private void CompletePermissionTools(string turnId) {
		AcpToolState[] tools;
		lock (_gate) tools = [.. _tools.Values.Where(tool => !tool.NotificationReported && tool.TurnId == turnId)];
		foreach (var tool in tools) CompleteToolMutations(tool);
	}

	private void CompletePermissionTool(AcpClientRequest request) {
		lock (_turnTransitionGate) {
			if (request.Method != "session/request_permission" || !OwnsGeneration(request.Generation)) return;
			string id = RequiredString(request.Parameters.GetProperty("toolCall"), "toolCallId", "permission tool");
			AcpToolState? tool;
			lock (_gate) _tools.TryGetValue(id, out tool);
			if (tool is { NotificationReported: false }) CompleteToolMutations(tool);
		}
	}

}
