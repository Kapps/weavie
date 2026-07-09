using System.Text.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Reads Codex app-server thread lifecycle responses.</summary>
internal static class CodexThreadResults {
	public static string ReadThreadId(JsonElement result) {
		if (!result.TryGetProperty("thread", out var thread)
			|| !thread.TryGetProperty("id", out var id)
			|| id.ValueKind != JsonValueKind.String
			|| string.IsNullOrEmpty(id.GetString())) {
			throw new InvalidOperationException("Codex app-server did not return a thread id.");
		}

		return id.GetString()!;
	}
}
