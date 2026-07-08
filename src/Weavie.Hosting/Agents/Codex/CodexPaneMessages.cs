using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Maps Codex app-server protocol objects into provider-neutral pane updates.</summary>
internal static class CodexPaneMessages {
	public static AgentPaneMessage? FromNotification(string method, string? threadId, JsonElement root) =>
		method switch {
			"turn/started" => FromTurn("turn-started", threadId, root),
			"turn/completed" => FromTurn("turn-completed", threadId, root),
			"turn/interrupted" => FromTurn("turn-interrupted", threadId, root),
			"item/started" => FromStartedItem(root),
			"item/completed" => FromCompletedItem(root),
			"item/fileChange/patchUpdated" => FromPatch(root),
			"turn/diff/updated" => FromDiff(root),
			"serverRequest/resolved" => FromResolved(root),
			"thread/status/changed" => FromStatus(root),
			"mcpServer/startupStatus/updated" => FromMcpStartupStatus(root),
			_ => null,
		};

	public static AgentPaneMessage FromRequest(CodexServerRequest request) =>
		FromRequest(request, request.Message.TryGetProperty("params", out var parameters) ? parameters : default);

	private static AgentPaneMessage FromRequest(CodexServerRequest request, JsonElement parameters) =>
		new() {
			Type = CodexApprovalResponses.CanResolve(request.Method) ? "approval-requested" : "input-requested",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			ItemId = request.Id,
			ItemType = request.Method,
			Summary = SummarizeRequest(request.Method, parameters),
			Status = "pending",
			PayloadJson = request.Message.GetRawText(),
		};

	private static string? SummarizeRequest(string method, JsonElement parameters) {
		string reason = parameters.GetStringOrEmpty("reason");
		if (reason.Length > 0 || !CodexInputResponses.CanResolve(method)) {
			return reason;
		}

		if (parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("questions", out var questions)
			|| questions.ValueKind != JsonValueKind.Array) {
			return null;
		}

		return string.Join(" ", questions.EnumerateArray()
			.Select(question => question.GetStringOrEmpty("question"))
			.Where(question => question.Length > 0));
	}

	private static AgentPaneMessage FromTurn(string type, string? threadId, JsonElement root) {
		var turn = root.GetProperty("params").GetProperty("turn");
		return new AgentPaneMessage {
			Type = type,
			ProviderId = "codex",
			ThreadId = threadId,
			TurnId = turn.GetStringOrEmpty("id"),
			Status = turn.GetStringOrEmpty("status"),
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage? FromStartedItem(JsonElement root) {
		var item = root.GetProperty("params").GetProperty("item");
		return IsVisibleStartedItem(item.GetStringOrEmpty("type")) ? FromItem("item-started", root) : null;
	}

	private static AgentPaneMessage? FromCompletedItem(JsonElement root) {
		var item = root.GetProperty("params").GetProperty("item");
		return IsVisibleCompletedItem(item.GetStringOrEmpty("type")) ? FromItem("item-completed", root) : null;
	}

	private static AgentPaneMessage FromItem(string type, JsonElement root) {
		var parameters = root.GetProperty("params");
		var item = parameters.GetProperty("item");
		string itemType = item.GetStringOrEmpty("type");
		return new AgentPaneMessage {
			Type = type,
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			ItemId = item.GetStringOrEmpty("id"),
			ItemType = itemType,
			Summary = SummarizeItem(itemType, item),
			Status = item.GetStringOrEmpty("status"),
			Text = item.GetStringOrEmpty("text"),
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage FromDiff(JsonElement root) {
		var parameters = root.GetProperty("params");
		return new AgentPaneMessage {
			Type = "turn-diff",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			Text = parameters.GetStringOrEmpty("diff"),
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage FromPatch(JsonElement root) {
		var parameters = root.GetProperty("params");
		return new AgentPaneMessage {
			Type = "file-patch-updated",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			ItemId = parameters.GetStringOrEmpty("itemId"),
			Summary = SummarizeChanges(parameters),
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage FromResolved(JsonElement root) {
		var parameters = root.GetProperty("params");
		return new AgentPaneMessage {
			Type = "approval-resolved",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			ItemId = parameters.TryGetProperty("requestId", out var id) ? id.GetRawText() : null,
			Status = "resolved",
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage FromStatus(JsonElement root) {
		var parameters = root.GetProperty("params");
		return new AgentPaneMessage {
			Type = "status",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			Status = parameters.GetStringOrEmpty("status"),
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage? FromMcpStartupStatus(JsonElement root) {
		var parameters = root.GetProperty("params");
		string status = parameters.GetStringOrEmpty("status");
		if (!string.Equals(status, "failed", StringComparison.Ordinal)) {
			return null;
		}

		string name = parameters.GetStringOrEmpty("name");
		string error = parameters.GetStringOrEmpty("error");
		return new AgentPaneMessage {
			Type = "warning",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			Summary = name.Length == 0 ? "MCP server failed" : $"MCP server '{name}' failed",
			Text = error.Length == 0 ? null : error,
			Status = "failed",
			PayloadJson = root.GetRawText(),
		};
	}

	private static string? SummarizeItem(string itemType, JsonElement item) =>
		itemType switch {
			"commandExecution" => item.GetStringOrEmpty("command"),
			"fileChange" => SummarizeFileChange(item),
			"mcpToolCall" => $"{item.GetStringOrEmpty("server")}.{item.GetStringOrEmpty("tool")}",
			"webSearch" => item.GetStringOrEmpty("query"),
			"agentMessage" => item.GetStringOrEmpty("text"),
			_ => itemType,
		};

	private static bool IsVisibleStartedItem(string itemType) =>
		itemType is "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or "webSearch";

	private static bool IsVisibleCompletedItem(string itemType) =>
		itemType == "agentMessage" || IsVisibleStartedItem(itemType);

	private static string SummarizeFileChange(JsonElement item) => SummarizeChanges(item);

	private static string SummarizeChanges(JsonElement item) {
		if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) {
			return "fileChange";
		}

		return string.Join(", ", changes.EnumerateArray().Select(change => change.GetStringOrEmpty("path")));
	}
}
