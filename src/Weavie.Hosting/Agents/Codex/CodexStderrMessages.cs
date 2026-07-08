using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Turns app-server stderr into user-facing diagnostics only when the line is actionable.</summary>
internal static class CodexStderrMessages {
	public static bool TryFromLine(string line, string? threadId, out AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(line);
		string text = StripPrefix(line);
		if (TryFromStructuredLog(text, threadId, out message)) {
			return true;
		}

		if (!text.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
			&& !text.Contains("fatal:", StringComparison.OrdinalIgnoreCase)) {
			message = null!;
			return false;
		}

		message = Error(threadId, "Codex app-server error", text);
		return true;
	}

	private static bool TryFromStructuredLog(string text, string? threadId, out AgentPaneMessage message) {
		if (!text.StartsWith('{')) {
			message = null!;
			return false;
		}

		try {
			using var doc = JsonDocument.Parse(text);
			string level = doc.RootElement.GetStringOrEmpty("level");
			if (!string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase)) {
				message = null!;
				return false;
			}

			var fields = doc.RootElement.TryGetProperty("fields", out var value) ? value : default;
			string summary = fields.GetStringOrEmpty("message");
			string error = fields.GetStringOrEmpty("error");
			message = ErrorWithPayload(
				threadId,
				summary.Length == 0 ? "Codex app-server error" : summary,
				error.Length == 0 ? null : error,
				doc.RootElement.GetRawText());
			return true;
		} catch (JsonException) {
			message = null!;
			return false;
		}
	}

	private static string StripPrefix(string line) {
		const string prefix = "[codex-app-server] ";
		return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line;
	}

	private static AgentPaneMessage Error(string? threadId, string summary, string? text) =>
		ErrorWithPayload(threadId, summary, text, null);

	private static AgentPaneMessage ErrorWithPayload(string? threadId, string summary, string? text, string? payload) =>
		new() {
			Type = "error",
			ProviderId = "codex",
			ThreadId = threadId,
			Summary = summary,
			Text = text,
			Status = "error",
			PayloadJson = payload,
		};
}
