using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// A parsed Claude Code hook event — the JSON Claude pipes to a <c>command</c> hook's stdin, relayed to
/// Weavie over the hook pipe. Carries enough to record a change (the tool plus its raw input) and to route
/// a decision. Malformed payloads parse to <see langword="null"/>, and the bridge then stays out of the way.
/// Non-tool events (turn boundaries like <c>UserPromptSubmit</c>/<c>Stop</c>) carry an empty tool name.
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
	/// For a <see cref="HookEventKind.SessionStart"/> event, why the conversation (re)started — <c>startup</c> /
	/// <c>resume</c> / <c>clear</c> / <c>compact</c>. Weavie acts on <c>clear</c>. Absent on other events.
	/// </summary>
	public string? Source { get; init; }

	/// <summary>The working directory the tool runs in, when present.</summary>
	public string? Cwd { get; init; }

	/// <summary>
	/// Claude Code's own permission mode at the time of the event (<c>default</c>/<c>acceptEdits</c>/<c>plan</c>/
	/// <c>bypassPermissions</c>), when the payload carries it. Claude OWNS this (the user cycles it with
	/// Shift+Tab); Weavie only OBSERVES it here to know whether edits are auto-applying. Absent on payloads
	/// that don't report it.
	/// </summary>
	public string? PermissionMode { get; init; }

	/// <summary>
	/// Parses a hook stdin payload. Returns <see langword="null"/> if the JSON is malformed, or for a
	/// <em>tool</em> event missing its tool name — the caller treats that as "no opinion", leaving Claude's
	/// normal flow untouched. Non-tool events (turn boundaries) parse with an empty tool name.
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
			// Non-tool events (turn boundaries UserPromptSubmit/Stop/Notification, and SessionStart) legitimately
			// carry no tool name; everything else — tool events and unrecognized junk — needs one, else there's
			// nothing to act on.
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
				Source = GetString(root, "source"),
				Cwd = GetString(root, "cwd"),
				PermissionMode = GetString(root, "permission_mode"),
			};
		} catch (JsonException) {
			return null;
		}
	}

	private static HookEventKind MapEvent(string? name) => name switch {
		"PreToolUse" => HookEventKind.PreToolUse,
		"PostToolUse" => HookEventKind.PostToolUse,
		"UserPromptSubmit" => HookEventKind.UserPromptSubmit,
		"Stop" => HookEventKind.Stop,
		"Notification" => HookEventKind.Notification,
		"SessionStart" => HookEventKind.SessionStart,
		_ => HookEventKind.Other,
	};

	private static string? GetString(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
