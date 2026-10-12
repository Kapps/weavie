using System.Text.Json;
using Weavie.Core.Hooks;

namespace Weavie.Core.Agents.Claude;

/// <summary>Maps a Claude tool call to the <see cref="AgentActivity"/> it describes.</summary>
public static class ClaudeToolActivity {
	/// <summary>The activity for <paramref name="request"/>'s tool and input.</summary>
	public static AgentActivity For(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		return request.ToolName switch {
			"Edit" or "Write" or "MultiEdit" => AgentActivity.OfFile(AgentActivityKind.Edit, Input(request, "file_path")),
			"NotebookEdit" => AgentActivity.OfFile(AgentActivityKind.Edit, Input(request, "notebook_path")),
			"Read" => AgentActivity.OfFile(AgentActivityKind.Read, Input(request, "file_path")),
			"Bash" => AgentActivity.Of(AgentActivityKind.Execute, Input(request, "command")),
			"Grep" or "Glob" => AgentActivity.Of(AgentActivityKind.Search, Input(request, "pattern")),
			"WebSearch" => AgentActivity.Of(AgentActivityKind.Search, Input(request, "query")),
			"WebFetch" => AgentActivity.Of(AgentActivityKind.Fetch, Input(request, "url")),
			"Task" or "Agent" => AgentActivity.Of(AgentActivityKind.Delegate, Input(request, "description")),
			_ => AgentActivity.Of(AgentActivityKind.Other, request.ToolName),
		};
	}

	/// <summary>The string <paramref name="key"/> of <paramref name="request"/>'s tool input, or <c>null</c> when absent or malformed.</summary>
	internal static string? Input(HookRequest request, string key) {
		try {
			using var doc = JsonDocument.Parse(request.ToolInputJson);
			return doc.RootElement.ValueKind == JsonValueKind.Object
				&& doc.RootElement.TryGetProperty(key, out var value)
				&& value.ValueKind == JsonValueKind.String
					? value.GetString()
					: null;
		} catch (JsonException) {
			return null;
		}
	}
}
