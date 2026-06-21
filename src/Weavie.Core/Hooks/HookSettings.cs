using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// Builds the per-instance Claude Code settings file (handed to the spawned claude via <c>--settings</c>)
/// whose only content is a <c>hooks</c> block routing PermissionRequest (the permission gate) and
/// PreToolUse/PostToolUse (the change feed) to the standalone hook relay. <c>--settings</c> merges additively,
/// so the user's own hooks still fire. Scoped to the child we spawn, so it never leaks to a hand-launched claude.
/// </summary>
public static class HookSettings {
	/// <summary>PermissionRequest matcher: every tool, so the permission gate can auto-allow any tool that would otherwise prompt. Fires only when a dialog would appear, so always-allowed tools (Read/Grep) cost nothing.</summary>
	public const string PermissionMatcher = "*";

	/// <summary>PreToolUse/PostToolUse matcher: the edit tools the change-tracking feed records (pre-edit baseline + post-edit result).</summary>
	public const string ObserveMatcher = "Write|Edit|MultiEdit|NotebookEdit";

	/// <summary>
	/// Renders the settings JSON. Every hook runs the standalone relay executable directly
	/// (<paramref name="relayBinaryPath"/>). The host resolves the relay and fails loudly when it is absent (see
	/// <c>IdeIntegration</c>), so there is no fallback to reason about here.
	/// </summary>
	/// <param name="relayBinaryPath">Absolute path to the standalone hook-relay executable.</param>
	public static string BuildJson(string relayBinaryPath) {
		ArgumentException.ThrowIfNullOrEmpty(relayBinaryPath);

		string command = $"\"{relayBinaryPath}\"";
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			writer.WriteStartObject("hooks");
			WriteEvent(writer, "PreToolUse", command, timeoutSeconds: 30, matcher: ObserveMatcher);
			WriteEvent(writer, "PostToolUse", command, timeoutSeconds: 30, matcher: ObserveMatcher);
			// PermissionRequest fires only when a tool would prompt, so the relay runs just for genuine permission
			// requests (never for auto-allowed Read/Grep). Matches every tool.
			WriteEvent(writer, "PermissionRequest", command, timeoutSeconds: 600, matcher: PermissionMatcher);
			// Turn-start boundary (not tool-scoped, so no matcher); short timeout since the handler just resets
			// the per-turn diff baseline in memory.
			WriteEvent(writer, "UserPromptSubmit", command, timeoutSeconds: 10, matcher: null);
			// Turn-end and attention boundaries drive the per-session status indicator (Idle/NeedsInput). Not
			// tool-scoped, so no matcher; short timeouts since the handlers only update in-memory session state.
			WriteEvent(writer, "Stop", command, timeoutSeconds: 10, matcher: null);
			WriteEvent(writer, "Notification", command, timeoutSeconds: 10, matcher: null);
			// All sources (no matcher): the first SessionStart of a launch/resume clears the session status from
			// Starting to Idle (claude is up and waiting), and a source=clear lets the resume store drop the stale
			// session id so a quit right after a clear cold-starts fresh. Both handlers filter on source themselves.
			// Short timeout since the handlers just update in-memory state.
			WriteEvent(writer, "SessionStart", command, timeoutSeconds: 10, matcher: null);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static void WriteEvent(Utf8JsonWriter writer, string eventName, string command, int timeoutSeconds, string? matcher) {
		writer.WriteStartArray(eventName);
		writer.WriteStartObject();
		if (matcher is not null) {
			writer.WriteString("matcher", matcher);
		}

		writer.WriteStartArray("hooks");
		writer.WriteStartObject();
		writer.WriteString("type", "command");
		writer.WriteString("command", command);
		writer.WriteNumber("timeout", timeoutSeconds);
		writer.WriteEndObject();
		writer.WriteEndArray();
		writer.WriteEndObject();
		writer.WriteEndArray();
	}
}
