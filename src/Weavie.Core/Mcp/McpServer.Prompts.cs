using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Mcp;

/// <summary>
/// An MCP prompt the registry server advertises. Claude Code surfaces each as a
/// <c>/mcp__weavie__&lt;name&gt;</c> slash command; invoking it fetches <see cref="Text"/> via
/// <c>prompts/get</c> and injects it into the conversation — no server-initiated model call.
/// </summary>
public sealed record McpPrompt {
	/// <summary>The prompt id, used in the slash command and <c>prompts/get</c>.</summary>
	public required string Name { get; init; }

	/// <summary>One-line human-facing description (shown in the slash-command list).</summary>
	public required string Description { get; init; }

	/// <summary>The instruction text injected as a single user message.</summary>
	public required string Text { get; init; }
}

// The prompts capability (registry mode only): prompts/list + prompts/get over the same JSON-RPC socket.
public sealed partial class McpServer {
	// The fixed Core prompt set advertised in registry mode. Currently just the workspace-setup prompt.
	private static readonly IReadOnlyList<McpPrompt> RegistryPrompts = [WorkspaceSetupPrompt.Prompt];

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

	private async Task HandlePromptsGetAsync(WebSocket ws, JsonElement root, string? idRaw, CancellationToken ct) {
		string? name = root.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var n)
			&& n.ValueKind == JsonValueKind.String ? n.GetString() : null;
		var prompt = _prompts.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
		if (prompt is null) {
			await SendErrorAsync(ws, idRaw, -32602, $"Unknown prompt: {name}", ct).ConfigureAwait(false);
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

		await SendResultAsync(ws, idRaw, Encoding.UTF8.GetString(stream.ToArray()), ct).ConfigureAwait(false);
	}
}
