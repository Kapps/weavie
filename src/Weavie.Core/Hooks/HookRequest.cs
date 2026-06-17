using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// A parsed Claude Code hook event — the JSON Claude pipes to a <c>command</c> hook's stdin, relayed to
/// Weavie over the hook pipe. Carries enough to record a change (the tool plus its raw input) and to route
/// a decision. Malformed / nameless payloads parse to <see langword="null"/>, and the bridge then stays out
/// of the way.
/// </summary>
public sealed record HookRequest {
	/// <summary>The hook event (pre/post tool use).</summary>
	public required HookEventKind Event { get; init; }

	/// <summary>The tool Claude is about to run / has run (e.g. <c>Bash</c>, <c>Edit</c>, <c>Write</c>).</summary>
	public required string ToolName { get; init; }

	/// <summary>The tool's input as raw JSON (e.g. <c>{"command":"…"}</c> for Bash, <c>{"file_path":…}</c> for Edit).</summary>
	public required string ToolInputJson { get; init; }

	/// <summary>Claude's session id, when present.</summary>
	public string? SessionId { get; init; }

	/// <summary>The working directory the tool runs in, when present.</summary>
	public string? Cwd { get; init; }

	/// <summary>
	/// Parses a hook stdin payload. Returns <see langword="null"/> if the JSON is malformed or missing the
	/// tool name — the caller treats that as "no opinion", leaving Claude's normal flow untouched.
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

			string? toolName = GetString(root, "tool_name");
			if (string.IsNullOrEmpty(toolName)) {
				return null;
			}

			string toolInput = root.TryGetProperty("tool_input", out var input) ? input.GetRawText() : "{}";

			return new HookRequest {
				Event = MapEvent(GetString(root, "hook_event_name")),
				ToolName = toolName,
				ToolInputJson = toolInput,
				SessionId = GetString(root, "session_id"),
				Cwd = GetString(root, "cwd"),
			};
		} catch (JsonException) {
			return null;
		}
	}

	private static HookEventKind MapEvent(string? name) => name switch {
		"PreToolUse" => HookEventKind.PreToolUse,
		"PostToolUse" => HookEventKind.PostToolUse,
		_ => HookEventKind.Other,
	};

	private static string? GetString(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
