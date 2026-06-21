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
	/// Renders the settings JSON for an apphost/self-contained build (Windows/macOS, and a published Linux
	/// exe), where the host executable is the relay directly. Equivalent to passing <see langword="null"/>
	/// for the entry assembly to <see cref="BuildJson(string, string?)"/>.
	/// </summary>
	/// <param name="hostExecutablePath">Absolute path to the Weavie host executable (the relay).</param>
	public static string BuildJson(string hostExecutablePath) => BuildJson(hostExecutablePath, null);

	/// <summary>
	/// Renders the settings JSON. The hook command launches the running host executable in relay mode
	/// (<c>"&lt;host&gt;" --hook-relay</c>), which dials the pipe named by <see cref="HookProtocol.PipeEnvVar"/>.
	/// When the host runs framework-dependent under the <c>dotnet</c> muxer (a dev <c>dotnet App.dll</c> run),
	/// <paramref name="hostEntryAssembly"/> is the managed entry <c>.dll</c> the muxer needs as its first
	/// argument — without it the command is a bare <c>"dotnet" --hook-relay</c>, which the muxer cannot launch.
	/// An apphost/self-contained build (Windows/macOS, and a published Linux exe) passes <see langword="null"/>.
	/// </summary>
	/// <param name="hostExecutablePath">Absolute path to the Weavie host executable (the relay).</param>
	/// <param name="hostEntryAssembly">The managed entry assembly to run when <paramref name="hostExecutablePath"/> is the dotnet muxer; otherwise <see langword="null"/>.</param>
	public static string BuildJson(string hostExecutablePath, string? hostEntryAssembly) {
		ArgumentException.ThrowIfNullOrEmpty(hostExecutablePath);

		string command = string.IsNullOrEmpty(hostEntryAssembly)
			? $"\"{hostExecutablePath}\" --hook-relay"
			: $"\"{hostExecutablePath}\" \"{hostEntryAssembly}\" --hook-relay";
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			writer.WriteStartObject("hooks");
			WriteEvent(writer, "PreToolUse", command, timeoutSeconds: 600, matcher: ToolMatcher);
			WriteEvent(writer, "PostToolUse", command, timeoutSeconds: 30, matcher: ToolMatcher);
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
