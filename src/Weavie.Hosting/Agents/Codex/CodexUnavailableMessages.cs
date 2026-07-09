using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents.Codex;

internal static class CodexUnavailableMessages {
	public static string SettingsFix(string reason, string settingsPath) =>
		"Native Codex could not start.\n\n"
		+ reason
		+ "\n\nSet codex.path in Weavie settings to a working codex.exe, then create a new Codex session.\n"
		+ $"Settings file: {settingsPath}\n"
		+ "Example: codex.path = 'C:\\Users\\you\\.codex\\packages\\standalone\\current\\bin\\codex.exe'";

	public static bool TryLaunchFailure(
		string line,
		string? threadId,
		string? currentPath,
		string settingsPath,
		out AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(line);
		ArgumentException.ThrowIfNullOrEmpty(settingsPath);
		string text = StripPrefix(line);
		if (!text.Contains("launch failed:", StringComparison.OrdinalIgnoreCase)) {
			message = null!;
			return false;
		}

		string current = string.IsNullOrWhiteSpace(currentPath) ? "(unset)" : currentPath;
		message = new AgentPaneMessage {
			Type = "error",
			ProviderId = "codex",
			ThreadId = threadId,
			Summary = "Codex app-server could not launch",
			Text = SettingsFix(text + $"\n\nCurrent codex.path: {current}", settingsPath),
			Status = "error",
		};
		return true;
	}

	private static string StripPrefix(string line) {
		const string prefix = "[codex-app-server] ";
		return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line;
	}
}
