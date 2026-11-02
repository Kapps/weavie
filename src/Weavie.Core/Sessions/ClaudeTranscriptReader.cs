using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Core.Sessions;

/// <summary>
/// Projects a Claude Code transcript (<c>.jsonl</c>) onto its live conversation branch: prompts, replies, and tool
/// calls by name. Sidechains, meta records, thinking, and tool-result-only records are not part of the projection.
/// </summary>
public static class ClaudeTranscriptReader {
	/// <summary>Reads the transcript text; a trailing line still being written is ignored, a malformed complete line throws.</summary>
	public static IReadOnlyList<AgentTranscriptEntry> Read(string jsonl) {
		ArgumentNullException.ThrowIfNull(jsonl);
		var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		string? last = null;
		string[] lines = jsonl.Split('\n');
		for (int index = 0; index < lines.Length - 1; index++) {
			if (lines[index].Trim().Length == 0) continue;
			JsonElement record;
			try {
				using var document = JsonDocument.Parse(lines[index]);
				record = document.RootElement.Clone();
			} catch (JsonException ex) {
				throw new InvalidDataException($"Claude transcript line {index + 1} is malformed: {ex.Message}", ex);
			}
			if (record.GetStringOrNull("uuid") is not { } uuid || record.GetBoolOrFalse("isSidechain")) continue;
			records[uuid] = record;
			last = uuid;
		}

		var branch = new List<JsonElement>();
		for (string? at = last; at is not null && records.Remove(at, out var record);) {
			branch.Add(record);
			at = record.GetStringOrNull("parentUuid") ?? record.GetStringOrNull("logicalParentUuid");
		}
		branch.Reverse();
		return [.. branch.SelectMany(Project)];
	}

	private static IEnumerable<AgentTranscriptEntry> Project(JsonElement record) {
		string type = record.GetStringOrEmpty("type");
		if (type is not ("user" or "assistant") || record.GetBoolOrFalse("isMeta")
			|| !record.TryGetProperty("message", out var message)
			|| !message.TryGetProperty("content", out var content)) yield break;
		var role = type == "user" ? AgentTranscriptRole.User : AgentTranscriptRole.Agent;
		if (content.ValueKind == JsonValueKind.String) {
			yield return new(role, content.GetString()!, null, null);
			yield break;
		}
		if (content.ValueKind != JsonValueKind.Array) yield break;
		foreach (var block in content.EnumerateArray()) {
			switch (block.GetStringOrEmpty("type")) {
				case "text" when block.GetStringOrEmpty("text") is { Length: > 0 } text:
					yield return new(role, text, null, null);
					break;
				case "tool_use":
					yield return new(AgentTranscriptRole.Tool, ToolSummary(block), null, null);
					break;
			}
		}
	}

	private static string ToolSummary(JsonElement block) {
		string name = block.GetStringOrEmpty("name");
		if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return name;
		string? detail = input.GetStringOrNull("description") ?? input.GetStringOrNull("file_path")
			?? input.GetStringOrNull("command") ?? input.GetStringOrNull("pattern");
		return detail is null ? name : $"{name}: {detail}";
	}
}
