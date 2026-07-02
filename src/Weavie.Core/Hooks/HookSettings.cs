using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// Builds the per-instance Claude Code <c>--settings</c> file whose only content is a <c>hooks</c> block
/// routing PermissionRequest (the gate) and PreToolUse/PostToolUse (the change feed + session status) to the
/// standalone relay. <c>--settings</c> merges additively (the user's own hooks still fire) and is scoped to
/// the child we spawn.
/// </summary>
public static class HookSettings {
	/// <summary>
	/// Matcher for every tool-scoped hook. PermissionRequest needs it so the gate can auto-allow anything that
	/// would prompt; PreToolUse/PostToolUse need it so the session status sees every tool start/finish (an
	/// approved permission prompt is only observable as the gated tool's PostToolUse). Consumers that only care
	/// about edits (change tracking, edit jump-links) filter by tool name themselves.
	/// </summary>
	public const string AllToolsMatcher = "*";

	/// <summary>
	/// Renders the settings JSON, every hook running the standalone relay at <paramref name="relayBinaryPath"/>.
	/// The host resolves the relay and fails loudly when it's absent (see <c>IdeIntegration</c>).
	/// </summary>
	/// <param name="relayBinaryPath">Absolute path to the standalone hook-relay executable.</param>
	public static string BuildJson(string relayBinaryPath) {
		ArgumentException.ThrowIfNullOrEmpty(relayBinaryPath);

		string command = $"\"{relayBinaryPath}\"";
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			writer.WriteStartObject("hooks");
			WriteEvent(writer, "PreToolUse", command, timeoutSeconds: 30, matcher: AllToolsMatcher);
			WriteEvent(writer, "PostToolUse", command, timeoutSeconds: 30, matcher: AllToolsMatcher);
			// 600s timeout: a permission request waits on the user.
			WriteEvent(writer, "PermissionRequest", command, timeoutSeconds: 600, matcher: AllToolsMatcher);
			// Turn-start boundary; resets the per-turn diff baseline.
			WriteEvent(writer, "UserPromptSubmit", command, timeoutSeconds: 10, matcher: null);
			// Turn-end and attention boundaries drive the per-session status indicator (Idle/NeedsInput).
			WriteEvent(writer, "Stop", command, timeoutSeconds: 10, matcher: null);
			WriteEvent(writer, "Notification", command, timeoutSeconds: 10, matcher: null);
			// All sources: clears status Starting → Idle, and source=clear lets the resume store drop its stale id.
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
