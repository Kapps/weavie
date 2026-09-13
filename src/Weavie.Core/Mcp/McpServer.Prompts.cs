using System.Text;
using System.Text.Json;

namespace Weavie.Core.Mcp;

// The prompts capability (registry mode only): prompts/list + prompts/get over the same JSON-RPC socket.
public sealed partial class McpServer {
	private string BuildPromptsListJson() {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
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
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private async Task HandlePromptsGetAsync(IMcpResponder responder, JsonElement root, string? idRaw, CancellationToken ct) {
		string? name = root.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var n)
			&& n.ValueKind == JsonValueKind.String ? n.GetString() : null;
		var prompt = _prompts.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
		if (prompt is null) {
			await responder.SendErrorAsync(idRaw, -32602, $"Unknown prompt: {name}", ct).ConfigureAwait(false);
			return;
		}

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
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
			writer.WriteEndObject();
		}

		await responder.SendResultAsync(idRaw, Encoding.UTF8.GetString(stream.ToArray()), ct).ConfigureAwait(false);
	}
}
