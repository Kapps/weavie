using System.Text.Json;
using Weavie.Core.Hooks;
using Weavie.Core.Review.Hints;

namespace Weavie.Core.Agents.Claude;

/// <summary>Translates Claude hook payloads into the provider-neutral facts Weavie consumes.</summary>
public static class ClaudeHookEventAdapter {
	/// <summary>Maps one parsed Claude hook request to its primary event, then any edit-disposition and review-findings events.</summary>
	public static IReadOnlyList<AgentEvent> Adapt(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		var events = new List<AgentEvent> { Primary(request) };
		if (!string.IsNullOrEmpty(request.PermissionMode)) {
			events.Add(new AgentEditDispositionObserved(request.PermissionMode));
		}
		if (request.Event == HookEventKind.PostToolUse && request.ToolName == ClaudeReportFindings.ToolName) {
			events.Add(ReviewFindings(request));
		}
		return events;
	}

	private static AgentEvent ReviewFindings(HookRequest request) {
		try {
			return new AgentReviewFindingsReported(ClaudeReportFindings.Parse(request.ToolInputJson), request.Cwd);
		} catch (FormatException ex) {
			return new AgentReviewFindingsUnreadable($"Claude reported review findings Weavie couldn't read: {ex.Message}");
		}
	}

	private static AgentEvent Primary(HookRequest request) => request.Event switch {
		HookEventKind.SessionStart => new AgentSessionStarted(request.Source),
		HookEventKind.UserPromptSubmit => new AgentPromptSubmitted(request.SessionId, request.Prompt),
		HookEventKind.Stop => new AgentTurnStopped(request.SessionWillResume),
		HookEventKind.Notification => new AgentNotification(request.Message),
		HookEventKind.PermissionRequest => new AgentPermissionRequested(),
		HookEventKind.PreToolUse => new AgentToolStarting(Mutation(request)),
		HookEventKind.PostToolUse => new AgentToolCompleted(Mutation(request)),
		_ => new AgentOtherEvent(),
	};

	private static AgentMutation Mutation(HookRequest request) {
		string? key = request.ToolName switch {
			"Edit" or "Write" or "MultiEdit" => "file_path",
			"NotebookEdit" => "notebook_path",
			_ => null,
		};
		if (key is null) {
			return new AgentMutation.None();
		}

		try {
			using var doc = JsonDocument.Parse(request.ToolInputJson);
			if (doc.RootElement.ValueKind == JsonValueKind.Object
				&& doc.RootElement.TryGetProperty(key, out var value)
				&& value.ValueKind == JsonValueKind.String
				&& value.GetString() is { Length: > 0 } path) {
				return new AgentMutation.File(path, request.Cwd, request.ToolName != "NotebookEdit");
			}
		} catch (JsonException) {
			// Malformed provider input retains the existing "not a trackable edit" behavior.
		}

		return new AgentMutation.None();
	}
}
