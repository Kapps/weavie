using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>
/// A minimal ACP agent that serves one consult turn. Its reply echoes what the client offered (cwd, MCP servers,
/// filesystem and terminal capabilities, model, consult framing) so a test can prove the consult's isolation.
/// </summary>
internal static class ConsultFake {
	private const int PermissionRequestId = 7001;

	public static async Task RunAsync(string variant) {
		string cwd = string.Empty;
		string model = "fake-model";
		int mcpServers = -1;
		bool fs = false;
		bool terminal = false;
		JsonNode? pendingPrompt = null;
		while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line) {
			if (line.Length == 0) continue;
			using var document = JsonDocument.Parse(line);
			var root = document.RootElement;
			var id = root.TryGetProperty("id", out var raw) ? JsonNode.Parse(raw.GetRawText()) : null;
			if (!root.TryGetProperty("method", out var methodElement)) {
				if (raw.ValueKind == JsonValueKind.Number && raw.GetInt32() == PermissionRequestId) {
					var outcome = root.GetProperty("result").GetProperty("outcome");
					Chunk($"permission={AcpJson.OptionalString(outcome, "optionId") ?? AcpJson.OptionalString(outcome, "outcome")}");
					Respond(pendingPrompt, new JsonObject { ["stopReason"] = "end_turn" });
				}
				continue;
			}

			var parameters = root.TryGetProperty("params", out var p) ? p : default;
			switch (methodElement.GetString()) {
				case "initialize":
					var capabilities = parameters.GetProperty("clientCapabilities");
					fs = capabilities.TryGetProperty("fs", out _);
					terminal = capabilities.TryGetProperty("terminal", out _);
					Respond(id, new JsonObject { ["protocolVersion"] = 1 });
					break;
				case "session/new" when variant == "auth":
					Write(new JsonObject {
						["jsonrpc"] = "2.0",
						["id"] = id,
						["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Authentication required" },
					});
					break;
				case "session/new":
					cwd = parameters.GetProperty("cwd").GetString() ?? string.Empty;
					mcpServers = parameters.GetProperty("mcpServers").GetArrayLength();
					Respond(id, new JsonObject { ["sessionId"] = "consult-session", ["configOptions"] = Models(model) });
					break;
				case "session/set_config_option":
					model = parameters.GetProperty("value").GetString() ?? string.Empty;
					Respond(id, new JsonObject { ["configOptions"] = Models(model) });
					break;
				case "session/prompt":
					pendingPrompt = id;
					string text = parameters.GetProperty("prompt")[0].GetProperty("text").GetString() ?? string.Empty;
					Prompt(variant, $"cwd={cwd};mcp={mcpServers};fs={fs};terminal={terminal};model={model};"
						+ $"framed={text.Contains("consulting you", StringComparison.Ordinal)};"
						+ $"asked={text[(text.LastIndexOf('\n') + 1)..]}", id);
					break;
				case "session/cancel":
					Respond(pendingPrompt, new JsonObject { ["stopReason"] = "cancelled" });
					break;
			}
		}
	}

	private static void Prompt(string variant, string echo, JsonNode? id) {
		switch (variant) {
			case "permission":
				Write(new JsonObject {
					["jsonrpc"] = "2.0",
					["id"] = PermissionRequestId,
					["method"] = "session/request_permission",
					["params"] = new JsonObject {
						["sessionId"] = "consult-session",
						["toolCall"] = new JsonObject { ["toolCallId"] = "t1", ["title"] = "Run the tests" },
						["options"] = new JsonArray(
							Option("yes", "allow_once"), Option("never", "reject_always"), Option("no", "reject_once")),
					},
				});
				break;
			case "edit":
				Update(new JsonObject {
					["sessionUpdate"] = "tool_call",
					["toolCallId"] = "t1",
					["title"] = "Edit a.cs",
					["kind"] = "edit",
					["locations"] = new JsonArray(new JsonObject { ["path"] = "/work/src/a.cs" }),
				});
				break;
			case "max-tokens":
				Chunk("partial");
				Respond(id, new JsonObject { ["stopReason"] = "max_tokens" });
				break;
			case "hang":
				break;
			default:
				Chunk("Let me look first.");
				Update(new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Read", ["kind"] = "read" });
				Chunk(echo);
				Respond(id, new JsonObject { ["stopReason"] = "end_turn" });
				break;
		}
	}

	private static JsonObject Option(string id, string kind) => new() { ["optionId"] = id, ["name"] = id, ["kind"] = kind };

	private static JsonArray Models(string model) => [
		new JsonObject {
			["id"] = "model",
			["name"] = "Model",
			["category"] = "model",
			["type"] = "select",
			["currentValue"] = model,
			["options"] = new JsonArray(
				new JsonObject { ["value"] = "fake-model", ["name"] = "Fake Model" },
				new JsonObject { ["value"] = "astra", ["name"] = "Astra", ["description"] = "The careful one" }),
		},
	];

	private static void Chunk(string text) => Update(new JsonObject {
		["sessionUpdate"] = "agent_message_chunk",
		["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
	});

	private static void Update(JsonObject update) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["method"] = "session/update",
		["params"] = new JsonObject { ["sessionId"] = "consult-session", ["update"] = update },
	});

	private static void Respond(JsonNode? id, JsonObject result) =>
		Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

	private static void Write(JsonObject payload) {
		Console.Out.WriteLine(payload.ToJsonString());
		Console.Out.Flush();
	}
}
