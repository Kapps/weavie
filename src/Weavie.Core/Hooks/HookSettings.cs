using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// Builds the per-instance Claude Code settings file (handed to the spawned claude via <c>--settings</c>)
/// whose only content is a <c>hooks</c> block routing PermissionRequest (the permission gate) and
/// PreToolUse/PostToolUse (the change feed) to the standalone hook relay. <c>--settings</c> merges additively,
/// so the user's own hooks still fire. Scope comes from the channel (the argv of the child we spawn), so this
/// never leaks to a claude the user launches by hand.
/// </summary>
public static class HookSettings {
	/// <summary>PermissionRequest matcher: every tool, so the permission gate (claude.allowAllTools) can auto-allow any tool that would otherwise prompt (PowerShell, WebFetch, MCP, ...). It fires only when a dialog would appear, so always-allowed tools (Read/Grep) cost nothing.</summary>
	public const string PermissionMatcher = "*";

	/// <summary>PreToolUse + PostToolUse matcher: the edit tools the change-tracking feed records (pre-edit baseline + post-edit result). The tracker ignores everything else, so this stays scoped to edits.</summary>
	public const string ObserveMatcher = "Write|Edit|MultiEdit|NotebookEdit";

	/// <summary>
	/// Renders the settings JSON. Every hook runs the standalone relay executable directly
	/// (<paramref name="relayBinaryPath"/>) — the dedicated binary the build co-locates with the host (Debug
	/// managed, Release NativeAOT), never the host re-invoking itself. The host resolves the relay and fails
	/// loudly when it is absent (see <c>IdeIntegration</c>), so there is no fallback to reason about here.
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
			// PermissionRequest is the permission GATE: it fires only when a tool would prompt, so the relay runs
			// just for genuine permission requests (never for auto-allowed Read/Grep). Matches every tool.
			WriteEvent(writer, "PermissionRequest", command, timeoutSeconds: 600, matcher: PermissionMatcher);
			// Turn-start boundary (no matcher — UserPromptSubmit isn't tool-scoped); short timeout since the
			// handler just resets the per-turn diff baseline in memory.
			WriteEvent(writer, "UserPromptSubmit", command, timeoutSeconds: 10, matcher: null);
			// Turn-end + attention boundaries drive the per-session status indicator (Idle / NeedsInput). Not
			// tool-scoped, so no matcher; short timeouts since the handlers only update in-memory session state.
			WriteEvent(writer, "Stop", command, timeoutSeconds: 10, matcher: null);
			WriteEvent(writer, "Notification", command, timeoutSeconds: 10, matcher: null);
			// /clear only: SessionStart matches on its source, so "clear" relays just the clears (not startup/
			// resume/compact). Lets the resume store drop the now-stale session id so a quit right after a clear
			// cold-starts fresh instead of resuming the cleared transcript. Short timeout — the handler just
			// updates the in-memory store.
			WriteEvent(writer, "SessionStart", command, timeoutSeconds: 10, matcher: "clear");
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
