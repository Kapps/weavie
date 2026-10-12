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
		if (request.Event is HookEventKind.PreToolUse or HookEventKind.PermissionRequest) {
			events.Add(new AgentActivityObserved(ClaudeToolActivity.For(request), request.Event == HookEventKind.PermissionRequest));
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
		return key is not null && ClaudeToolActivity.Input(request, key) is { Length: > 0 } path
			? new AgentMutation.File(path, request.Cwd, request.ToolName != "NotebookEdit")
			: new AgentMutation.None();
	}
}
