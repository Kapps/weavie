using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>
/// Builds the per-instance Claude Code settings file (handed to the spawned claude via <c>--settings</c>)
/// whose only content is a <c>hooks</c> block routing PreToolUse/PostToolUse for the mutating tools to the
/// hook relay. <c>--settings</c> merges additively, so the user's own hooks still fire. Scope comes from the
/// channel (the argv of the child we spawn), so this never leaks to a claude the user launches by hand.
/// </summary>
public static class HookSettings {
	/// <summary>The tools whose calls are routed to the bridge — the ones that can mutate the workspace.</summary>
	public const string ToolMatcher = "Bash|Write|Edit|MultiEdit|NotebookEdit";

	/// <summary>
	/// Renders the settings JSON. The hook command launches the running host executable in relay mode
	/// (<c>"&lt;host&gt;" --hook-relay</c>), which dials the pipe named by <see cref="HookProtocol.PipeEnvVar"/>.
	/// </summary>
	/// <param name="hostExecutablePath">Absolute path to the Weavie host executable (the relay).</param>
	public static string BuildJson(string hostExecutablePath) {
		ArgumentException.ThrowIfNullOrEmpty(hostExecutablePath);

		string command = $"\"{hostExecutablePath}\" --hook-relay";
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			writer.WriteStartObject("hooks");
			WriteEvent(writer, "PreToolUse", command, timeoutSeconds: 600);
			WriteEvent(writer, "PostToolUse", command, timeoutSeconds: 30);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static void WriteEvent(Utf8JsonWriter writer, string eventName, string command, int timeoutSeconds) {
		writer.WriteStartArray(eventName);
		writer.WriteStartObject();
		writer.WriteString("matcher", ToolMatcher);
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
