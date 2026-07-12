using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Maps Codex app-server protocol objects into provider-neutral pane updates.</summary>
internal static class CodexPaneMessages {
	public static AgentPaneMessage? FromNotification(string method, string? _, JsonElement root) =>
		method switch {
			"error" => FromError(root),
			"turn/started" => FromTurn("turn-started", root),
			"turn/completed" => FromTurn("turn-completed", root),
			"item/started" => FromStartedItem(root),
			"item/completed" => FromCompletedItem(root),
			"item/agentMessage/delta" => FromDelta("agent-message-delta", "agentMessage", root),
			"item/plan/delta" => FromDelta("plan-delta", "plan", root),
			"item/commandExecution/outputDelta" => FromDelta("command-output-delta", "commandExecution", root),
			"item/fileChange/patchUpdated" => FromPatch(root),
			"turn/diff/updated" => FromDiff(root),
			"serverRequest/resolved" => FromResolved(root),
			"mcpServer/startupStatus/updated" => FromMcpStartupStatus(root),
			_ => null,
		};

	public static IReadOnlyList<AgentPaneMessage> FromThreadSnapshot(JsonElement result) {
		if (!result.TryGetProperty("thread", out var thread)
			|| !thread.TryGetProperty("turns", out var turns)
			|| turns.ValueKind != JsonValueKind.Array
			|| turns.GetArrayLength() == 0) {
			return [];
		}

		string threadId = thread.GetStringOrEmpty("id");
		var messages = new List<AgentPaneMessage>();
		foreach (var turn in turns.EnumerateArray()) {
			string turnId = turn.GetStringOrEmpty("id");
			var error = turn.TryGetProperty("error", out var turnError) ? turnError : default;
			if (turn.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) {
				foreach (var item in items.EnumerateArray()) {
					messages.AddRange(FromHistoryItem(threadId, turnId, item));
				}
			}

			string status = turn.GetStringOrEmpty("status");
			messages.Add(new AgentPaneMessage {
				Type = string.Equals(status, "inProgress", StringComparison.Ordinal) ? "turn-started" : "turn-completed",
				ProviderId = "codex",
				ThreadId = threadId,
				TurnId = turnId,
				Status = status,
				Summary = error.ValueKind == JsonValueKind.Object ? ErrorSummary(error) : null,
				Text = error.ValueKind == JsonValueKind.Object ? ErrorText(error) : null,
			});
		}
		return messages;
	}

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
			Category = CodexApprovalResponses.CanResolve(request.Method) ? "permission" : "input",
			Summary = SummarizeRequest(request.Method, parameters),
			Status = "pending",
			Text = RequestText(request.Method, parameters),
			Questions = ReadQuestions(parameters),
			PayloadJson = request.Message.GetRawText(),
		};

	/// <summary>The substance being approved — the user must see what they are consenting to, not just why.</summary>
	private static string? RequestText(string method, JsonElement parameters) {
		if (parameters.ValueKind != JsonValueKind.Object) {
			return null;
		}

		// fileChange approvals carry no substance in params (only item ids); the session joins the paths
		// from the item's own notifications before emitting the card.
		return method switch {
			"item/commandExecution/requestApproval" => NormalizeToNull(parameters.GetStringOrEmpty("command")),
			_ => null,
		};
	}

	private static string? NormalizeToNull(string value) => value.Length == 0 ? null : value;

	private static IReadOnlyList<AgentInputQuestion>? ReadQuestions(JsonElement parameters) {
		if (parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("questions", out var questions)
			|| questions.ValueKind != JsonValueKind.Array) {
			return null;
		}

		return questions.EnumerateArray().Select(question => new AgentInputQuestion {
			Id = question.GetStringOrEmpty("id"),
			Header = question.GetStringOrEmpty("header"),
			Question = question.GetStringOrEmpty("question"),
			IsSecret = question.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True,
			Options = ReadOptions(question),
		}).Where(question => question.Id.Length > 0 && question.Question.Length > 0).ToArray();
	}

	private static IReadOnlyList<AgentInputOption> ReadOptions(JsonElement question) {
		if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) {
			return [];
		}

		return options.EnumerateArray().Select(option => new AgentInputOption {
			Label = option.GetStringOrEmpty("label"),
			Description = option.GetStringOrEmpty("description"),
		}).Where(option => option.Label.Length > 0).ToArray();
	}

	private static string? SummarizeRequest(string method, JsonElement parameters) {
		string reason = parameters.GetStringOrEmpty("reason");
		if (reason.Length > 0) {
			return reason;
		}
		if (string.Equals(method, "mcpServer/elicitation/request", StringComparison.Ordinal)) {
			string message = parameters.GetStringOrEmpty("message");
			return message.Length == 0 ? null : message;
		}
		if (!CodexInputResponses.CanResolve(method)) {
			return null;
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

	private static AgentPaneMessage? FromStartedItem(JsonElement root) {
		var item = root.GetProperty("params").GetProperty("item");
		return IsVisibleStartedItem(item.GetStringOrEmpty("type")) ? FromItem("item-started", root) : null;
	}

	private static AgentPaneMessage? FromCompletedItem(JsonElement root) {
		var item = root.GetProperty("params").GetProperty("item");
		return IsVisibleCompletedItem(item.GetStringOrEmpty("type")) ? FromItem("item-completed", root) : null;
	}

	private static AgentPaneMessage FromDelta(string type, string itemType, JsonElement root) {
		var parameters = root.GetProperty("params");
		return new AgentPaneMessage {
			Type = type,
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			ItemId = parameters.GetStringOrEmpty("itemId"),
			ItemType = itemType,
			Category = ItemCategory(itemType),
			Text = parameters.GetStringOrEmpty("delta"),
			Status = "inProgress",
		};
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
			Category = ItemCategory(itemType),
			Summary = SummarizeItem(itemType, item),
			Status = item.GetStringOrEmpty("status"),
			Text = ItemText(itemType, item),
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
			Category = "diff",
			Text = parameters.GetStringOrEmpty("diff"),
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage FromTurn(string type, JsonElement root) {
		var parameters = root.GetProperty("params");
		var turn = parameters.TryGetProperty("turn", out var value) ? value : default;
		var error = turn.ValueKind == JsonValueKind.Object && turn.TryGetProperty("error", out var turnError)
			? turnError
			: default;
		return new AgentPaneMessage {
			Type = type,
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = turn.ValueKind == JsonValueKind.Object
				? turn.GetStringOrEmpty("id")
				: parameters.GetStringOrEmpty("turnId"),
			Status = turn.ValueKind == JsonValueKind.Object
				? turn.GetStringOrEmpty("status")
				: parameters.GetStringOrEmpty("status"),
			Summary = error.ValueKind == JsonValueKind.Object ? ErrorSummary(error) : null,
			Text = error.ValueKind == JsonValueKind.Object ? ErrorText(error) : null,
			PayloadJson = root.GetRawText(),
		};
	}

	private static AgentPaneMessage FromError(JsonElement root) {
		var parameters = root.GetProperty("params");
		var error = parameters.TryGetProperty("error", out var value) ? value : default;
		bool willRetry = parameters.GetBoolOrFalse("willRetry");
		return new AgentPaneMessage {
			Type = willRetry ? "warning" : "error",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			Summary = ErrorSummary(error),
			Text = ErrorText(error),
			Status = willRetry ? "retrying" : "failed",
			PayloadJson = root.GetRawText(),
		};
	}

	private static string ErrorSummary(JsonElement error) {
		string info = error.GetStringOrEmpty("codexErrorInfo");
		return info switch {
			"contextWindowExceeded" => "Conversation is too long",
			"usageLimitExceeded" => "Codex usage limit reached",
			"serverOverloaded" => "Codex is temporarily overloaded",
			"unauthorized" => "Codex authentication failed",
			_ => "Codex could not complete the turn",
		};
	}

	private static string? ErrorText(JsonElement error) {
		string message = error.GetStringOrEmpty("message");
		string details = error.GetStringOrEmpty("additionalDetails");
		if (message.Length == 0) {
			return details.Length == 0 ? null : details;
		}
		return details.Length == 0 || string.Equals(message, details, StringComparison.Ordinal)
			? message
			: $"{message}\n{details}";
	}

	private static AgentPaneMessage FromPatch(JsonElement root) {
		var parameters = root.GetProperty("params");
		return new AgentPaneMessage {
			Type = "file-patch-updated",
			ProviderId = "codex",
			ThreadId = parameters.GetStringOrEmpty("threadId"),
			TurnId = parameters.GetStringOrEmpty("turnId"),
			ItemId = parameters.GetStringOrEmpty("itemId"),
			Category = "edit",
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
			TurnId = parameters.GetStringOrEmpty("turnId"),
			ItemId = parameters.TryGetProperty("requestId", out var id) ? CodexAppServerClient.ReadRequestId(id) : null,
			Status = "resolved",
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
		if (string.Equals(name, "github", StringComparison.OrdinalIgnoreCase)
			&& error.Contains("CODEX_GITHUB_PERSONAL_ACCESS_TOKEN", StringComparison.Ordinal)) {
			return new AgentPaneMessage {
				Type = "warning",
				ProviderId = "codex",
				ThreadId = parameters.GetStringOrEmpty("threadId"),
				Summary = "GitHub MCP is not authenticated",
				Text = "Set CODEX_GITHUB_PERSONAL_ACCESS_TOKEN or disable the Codex github MCP server.",
				Status = "failed",
				PayloadJson = root.GetRawText(),
			};
		}

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
			"commandExecution" => SummarizeCommand(item.GetStringOrEmpty("command")),
			"fileChange" => SummarizeFileChange(item),
			"mcpToolCall" => $"{item.GetStringOrEmpty("server")}.{item.GetStringOrEmpty("tool")}",
			"webSearch" => item.GetStringOrEmpty("query"),
			"agentMessage" => null,
			_ => itemType,
		};

	private static string ItemCategory(string itemType) => itemType switch {
		"commandExecution" => "command",
		"fileChange" => "edit",
		"mcpToolCall" or "dynamicToolCall" => "tool",
		"webSearch" => "search",
		"agentMessage" => "message",
		"plan" => "plan",
		_ => "step",
	};

	private static string? ItemText(string itemType, JsonElement item) {
		string text = itemType == "commandExecution"
			? item.GetStringOrEmpty("aggregatedOutput")
			: item.GetStringOrEmpty("text");
		return text.Length == 0 ? null : text;
	}

	private static bool IsVisibleStartedItem(string itemType) =>
		itemType is "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or "webSearch";

	private static bool IsVisibleCompletedItem(string itemType) =>
		itemType is "agentMessage" or "plan" || IsVisibleStartedItem(itemType);

	private static IEnumerable<AgentPaneMessage> FromHistoryItem(string threadId, string turnId, JsonElement item) {
		string itemType = item.GetStringOrEmpty("type");
		string itemId = item.GetStringOrEmpty("id");
		if (itemType == "userMessage") {
			if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) {
				yield break;
			}
			string text = string.Join("\n", content.EnumerateArray()
				.Where(value => value.GetStringOrEmpty("type") == "text")
				.Select(value => value.GetStringOrEmpty("text"))
				.Where(value => value.Length > 0));
			if (text.Length > 0) {
				yield return new AgentPaneMessage {
					Type = "user-message",
					ProviderId = "codex",
					ThreadId = threadId,
					TurnId = turnId,
					ItemId = itemId,
					Text = text,
				};
			}

			int imageIndex = 0;
			foreach (var image in content.EnumerateArray().Where(value =>
				value.GetStringOrEmpty("type") is "image" or "localImage")) {
				yield return new AgentPaneMessage {
					Type = "user-image",
					ProviderId = "codex",
					ThreadId = threadId,
					TurnId = turnId,
					ItemId = $"{itemId}:image:{imageIndex++}",
					Text = image.GetStringOrEmpty(image.GetStringOrEmpty("type") == "localImage" ? "path" : "url"),
					Status = "submitted",
				};
			}
			yield break;
		}

		if (!IsVisibleCompletedItem(itemType)) {
			yield break;
		}
		yield return new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = "codex",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = itemType,
			Category = ItemCategory(itemType),
			Summary = SummarizeItem(itemType, item),
			Status = item.GetStringOrEmpty("status") is { Length: > 0 } status ? status : "completed",
			Text = ItemText(itemType, item),
			PayloadJson = item.GetRawText(),
		};
	}

	private static string SummarizeFileChange(JsonElement item) => SummarizeChanges(item);

	private static string? SummarizeCommand(string command) {
		if (command.Length == 0) {
			return null;
		}

		const string marker = " -Command ";
		int commandStart = command.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		return TrimCommand(commandStart >= 0 ? command[(commandStart + marker.Length)..] : command);
	}

	private static string TrimCommand(string command) {
		string trimmed = command.Trim();
		if (trimmed.Length >= 2
			&& ((trimmed[0] == '\'' && trimmed[^1] == '\'') || (trimmed[0] == '"' && trimmed[^1] == '"'))) {
			return trimmed[1..^1];
		}

		return trimmed;
	}

	private static string SummarizeChanges(JsonElement item) {
		if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) {
			return "fileChange";
		}

		return string.Join(", ", changes.EnumerateArray().Select(change => change.GetStringOrEmpty("path")));
	}
}
