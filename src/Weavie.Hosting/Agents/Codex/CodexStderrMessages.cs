using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Turns app-server stderr into user-facing diagnostics only when the line is actionable.</summary>
internal static class CodexStderrMessages {
	public static bool TryFromLine(string line, string? threadId, out AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(line);
		string text = StripAnsi(StripPrefix(line));
		if (IsDuplicateMcpAuthFailure(text)) {
			message = null!;
			return false;
		}

		if (TryFromStructuredLog(text, threadId, out message)) {
			return true;
		}

		message = null!;
		return false;
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
			if (error.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase)) {
				message = null!;
				return false;
			}

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

	private static string StripAnsi(string text) {
		char[] result = new char[text.Length];
		int length = 0;
		for (int i = 0; i < text.Length; i++) {
			if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '[') {
				i += 2;
				while (i < text.Length && !char.IsLetter(text[i])) {
					i++;
				}
				continue;
			}

			result[length++] = text[i];
		}

		return new string(result, 0, length);
	}

	private static bool IsDuplicateMcpAuthFailure(string text) =>
		text.Contains("api.githubcopilot.com/.well-known/oauth-protected-resource/mcp/", StringComparison.OrdinalIgnoreCase)
		&& text.Contains("No access token was provided", StringComparison.OrdinalIgnoreCase);

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
