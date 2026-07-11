using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// A parsed Claude Code hook event (the JSON Claude pipes to a <c>command</c> hook's stdin), carrying enough
/// to record a change and route a decision. Non-tool events (turn boundaries) carry an empty tool name.
/// </summary>
public sealed record HookRequest {
	/// <summary>The hook event (pre/post tool use, or a turn boundary).</summary>
	public required HookEventKind Event { get; init; }

	/// <summary>The tool Claude is about to run / has run (e.g. <c>Bash</c>, <c>Edit</c>, <c>Write</c>); empty for non-tool events.</summary>
	public required string ToolName { get; init; }

	/// <summary>The tool's input as raw JSON (e.g. <c>{"command":"…"}</c> for Bash, <c>{"file_path":…}</c> for Edit).</summary>
	public required string ToolInputJson { get; init; }

	/// <summary>Claude's session id, when present.</summary>
	public string? SessionId { get; init; }

	/// <summary>
	/// For a <see cref="HookEventKind.Notification"/> event, the notice text — lets the status machine tell a
	/// permission prompt apart from the post-turn idle "waiting for your input" notice. Absent on other events.
	/// </summary>
	public string? Message { get; init; }

	/// <summary>
	/// For a <see cref="HookEventKind.SessionStart"/> event, why the conversation (re)started (<c>startup</c>/
	/// <c>resume</c>/<c>clear</c>/<c>compact</c>); Weavie acts on <c>clear</c>. Absent on other events.
	/// </summary>
	public string? Source { get; init; }

	/// <summary>
	/// For a <see cref="HookEventKind.UserPromptSubmit"/> event, the submitted prompt text — the attribution a
	/// recorded correction carries into the /learn corpus. Absent on other events.
	/// </summary>
	public string? Prompt { get; init; }

	/// <summary>The working directory the tool runs in, when present.</summary>
	public string? Cwd { get; init; }

	/// <summary>
	/// Claude's own permission mode at the event (<c>default</c>/<c>acceptEdits</c>/<c>plan</c>/
	/// <c>bypassPermissions</c>), when present — Weavie observes it to know whether edits are auto-applying.
	/// </summary>
	public string? PermissionMode { get; init; }

	/// <summary>
	/// For a <see cref="HookEventKind.Stop"/> event: whether the session will resume itself after this turn — a
	/// pending one-shot scheduled wakeup (a <c>session_crons</c> entry with <c>recurring:false</c>, from
	/// ScheduleWakeup / a dynamic loop) or an in-flight <c>background_tasks</c> entry. Idle to the eye but not
	/// done, so the update drain holds for it (a restart would kill the pending step). A recurring cron is
	/// excluded — a standing routine would hold the update forever. Always false on other events.
	/// </summary>
	public bool SessionWillResume { get; init; }

	/// <summary>
	/// Parses a hook stdin payload. Returns <see langword="null"/> for malformed JSON or a tool event missing
	/// its tool name (treated as "no opinion"). Non-tool events parse with an empty tool name.
	/// </summary>
	/// <param name="json">The JSON text Claude wrote to the hook's stdin.</param>
	public static HookRequest? Parse(string json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				return null;
			}

			var evt = MapEvent(GetString(root, "hook_event_name"));
			string? toolName = GetString(root, "tool_name");
			// Turn boundaries and SessionStart legitimately carry no tool name; everything else needs one,
			// else there's nothing to act on.
			bool isNonToolEvent = evt is HookEventKind.UserPromptSubmit or HookEventKind.Stop
				or HookEventKind.Notification or HookEventKind.SessionStart;
			if (!isNonToolEvent && string.IsNullOrEmpty(toolName)) {
				return null;
			}

			string toolInput = root.TryGetProperty("tool_input", out var input) ? input.GetRawText() : "{}";

			return new HookRequest {
				Event = evt,
				ToolName = toolName ?? string.Empty,
				ToolInputJson = toolInput,
				SessionId = GetString(root, "session_id"),
				Message = GetString(root, "message"),
				Source = GetString(root, "source"),
				Prompt = evt == HookEventKind.UserPromptSubmit ? GetString(root, "prompt") : null,
				Cwd = GetString(root, "cwd"),
				PermissionMode = GetString(root, "permission_mode"),
				SessionWillResume = evt == HookEventKind.Stop && StopHasPendingResumption(root),
			};
		} catch (JsonException) {
			return null;
		}
	}

	private static HookEventKind MapEvent(string? name) => name switch {
		"PreToolUse" => HookEventKind.PreToolUse,
		"PostToolUse" => HookEventKind.PostToolUse,
		"PermissionRequest" => HookEventKind.PermissionRequest,
		"UserPromptSubmit" => HookEventKind.UserPromptSubmit,
		"Stop" => HookEventKind.Stop,
		"Notification" => HookEventKind.Notification,
		"SessionStart" => HookEventKind.SessionStart,
		_ => HookEventKind.Other,
	};

	private static string? GetString(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	// A Stop payload lists the session's in-flight background tasks and scheduled crons (both present, empty when
	// none). The session resumes itself if a background task is still in flight, or a one-shot wakeup is pending;
	// a recurring cron is a standing routine that would never clear the hold, so it doesn't count.
	private static bool StopHasPendingResumption(JsonElement root) =>
		HasEntry(root, "background_tasks", static _ => true)
		|| HasEntry(root, "session_crons",
			static cron => !cron.TryGetProperty("recurring", out var recurring) || recurring.ValueKind != JsonValueKind.True);

	// True when <paramref name="property"/> is an array holding at least one object element matching <paramref name="match"/>.
	private static bool HasEntry(JsonElement root, string property, Func<JsonElement, bool> match) {
		if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) {
			return false;
		}

		foreach (var entry in array.EnumerateArray()) {
			if (entry.ValueKind == JsonValueKind.Object && match(entry)) {
				return true;
			}
		}

		return false;
	}
}
