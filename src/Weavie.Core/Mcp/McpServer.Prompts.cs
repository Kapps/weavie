using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Mcp;

// The bundled workflows (registry mode only): prompts/list + prompts/get for user-invoked slash commands, and the
// runWorkflow tool so an agent can start the same workflow when the user asks for it in plain language.
public sealed partial class McpServer {
	private string BuildPromptsListJson() => JsonWrite.Object(writer => {
		writer.WriteStartArray("prompts");
		foreach (var prompt in _prompts) {
			writer.WriteStartObject();
			writer.WriteString("name", prompt.Name);
			writer.WriteString("description", prompt.Description);
			writer.WriteStartArray("arguments"); // no arguments; the prompt inspects the repo itself
			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
	});

	private string BuildWorkflowToolEntry() => JsonWrite.Object(writer => {
		writer.WriteString("name", "runWorkflow");
		writer.WriteString("description",
			"Get the instructions for one of Weavie's bundled workflows, then follow them yourself in this turn. "
			+ "Call it when the user asks for one of these in their own words (e.g. to report a Weavie bug or request "
			+ "a Weavie feature):\n"
			+ string.Join("\n", _prompts.Select(prompt => $"- {prompt.Name}: {prompt.Description}")));
		writer.WriteStartObject("inputSchema");
		writer.WriteString("type", "object");
		writer.WriteStartObject("properties");
		writer.WriteStartObject("name");
		writer.WriteString("type", "string");
		writer.WriteStartArray("enum");
		foreach (var prompt in _prompts) writer.WriteStringValue(prompt.Name);
		writer.WriteEndArray();
		writer.WriteEndObject();
		writer.WriteEndObject();
		writer.WriteStartArray("required");
		writer.WriteStringValue("name");
		writer.WriteEndArray();
		writer.WriteEndObject();
		writer.WriteStartObject("annotations"); // returns text only, so agents need not ask permission to read it
		writer.WriteBoolean("readOnlyHint", true);
		writer.WriteEndObject();
	});

	private McpPrompt? FindPrompt(string? name) =>
		_prompts.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

	private async Task HandleRunWorkflowAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string name = args.GetStringOrEmpty("name");
		await (FindPrompt(name) is { } prompt
			? SendToolTextAsync(responder, idRaw, prompt.Text, ct)
			: SendToolErrorAsync(responder, idRaw,
				$"Unknown workflow '{name}'. Available: {string.Join(", ", _prompts.Select(p => p.Name))}.", ct)).ConfigureAwait(false);
	}

	private async Task HandlePromptsGetAsync(IMcpResponder responder, JsonElement root, string? idRaw, CancellationToken ct) {
		string? name = root.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var n)
			&& n.ValueKind == JsonValueKind.String ? n.GetString() : null;
		if (FindPrompt(name) is not { } prompt) {
			await responder.SendErrorAsync(idRaw, -32602, $"Unknown prompt: {name}", ct).ConfigureAwait(false);
			return;
		}

		await responder.SendResultAsync(idRaw, JsonWrite.Object(writer => {
			writer.WriteString("description", prompt.Description);
			writer.WriteStartArray("messages");
			writer.WriteStartObject();
			writer.WriteString("role", "user");
			writer.WriteStartObject("content");
			writer.WriteString("type", "text");
			writer.WriteString("text", prompt.Text);
			writer.WriteEndObject();
			writer.WriteEndObject();
			writer.WriteEndArray();
		}), ct).ConfigureAwait(false);
	}
}
