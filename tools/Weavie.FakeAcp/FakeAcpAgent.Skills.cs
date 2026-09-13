using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	private string? _skillCatalog;

	private async Task ReadBundledSkillAsync(JsonElement prompt, string name, CancellationToken ct) {
		foreach (var block in prompt.EnumerateArray()) {
			string? text = block.TryGetProperty("resource", out var resource)
				? AcpJson.OptionalString(resource, "text")
				: AssistantAudience(block) ? AcpJson.OptionalString(block, "text") : null;
			if (text?.Contains("## Weavie-only skills", StringComparison.Ordinal) == true) _skillCatalog = text;
		}
		string entry = (_skillCatalog ?? throw new InvalidOperationException("No bundled skill catalog received."))
			.Split('\n').Single(line => line.StartsWith($"- {name}: ", StringComparison.Ordinal));
		int pathStart = entry.LastIndexOf("(file: ", StringComparison.Ordinal) + "(file: ".Length;
		string path = entry[pathStart..^1];
		string skill = await ReadSkillFileAsync(path, name, ct).ConfigureAwait(false);
		var references = Regex.Matches(skill, @"\]\(([^)]+\.md)\)");
		foreach (Match reference in references) {
			string referencePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference.Groups[1].Value));
			string instructions = await ReadSkillFileAsync(referencePath, Path.GetFileName(referencePath), ct).ConfigureAwait(false);
			Message($"Read shared instructions through ACP: {Path.GetFileName(referencePath)}\n\n{instructions}");
		}
		Message($"Deterministic skill-file check completed: {name}. Read SKILL.md and {references.Count} reference(s) through ACP. No issue submitted; this checks file delivery, not model reasoning.");
	}

	private async Task<string> ReadSkillFileAsync(string path, string title, CancellationToken ct) {
		string callId = Guid.NewGuid().ToString("N");
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = callId,
			["title"] = $"Read bundled skill: {title}",
			["kind"] = "read",
			["status"] = "in_progress",
		});
		var result = await Connection().RequestAsync("fs/read_text_file", new JsonObject {
			["sessionId"] = _sessionId,
			["path"] = path,
		}, ct).ConfigureAwait(false);
		string content = result.GetProperty("content").GetString()
			?? throw new InvalidOperationException($"No content returned for {path}.");
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = callId,
			["status"] = "completed",
		});
		return content;
	}

	private static bool AssistantAudience(JsonElement block) =>
		block.TryGetProperty("annotations", out var annotations)
		&& annotations.TryGetProperty("audience", out var audience)
		&& audience.GetArrayLength() == 1 && audience[0].GetString() == "assistant";
}
