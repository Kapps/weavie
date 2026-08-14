using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal static class MixedRequestIdFake {
	public static async Task RunAsync() {
		string sessionId = "mixed-id-session";
		while (await ReadAsync().ConfigureAwait(false) is { } message) {
			if (!message.TryGetProperty("method", out var methodValue)) continue;
			string method = methodValue.GetString() ?? throw new JsonException("ACP method cannot be null.");
			if (!message.TryGetProperty("id", out var id)) continue;
			switch (method) {
				case "initialize":
					Respond(id, Initialize());
					break;
				case "session/new":
					Respond(id, Setup(sessionId));
					break;
				case "session/prompt":
					await PromptAsync(id, sessionId).ConfigureAwait(false);
					break;
				case "session/close":
					Respond(id, new JsonObject());
					return;
				default:
					throw new JsonException($"Unexpected ACP request '{method}'.");
			}
		}
	}

	private static async Task PromptAsync(JsonElement promptId, string sessionId) {
		string cwd = Environment.CurrentDirectory;
		Request(JsonValue.Create(1)!, sessionId, Path.Combine(cwd, "mixed-number.txt"));
		Request(JsonValue.Create("1")!, sessionId, Path.Combine(cwd, "mixed-string.txt"));
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		while (values.Count < 2) {
			var response = await ReadAsync().ConfigureAwait(false)
				?? throw new EndOfStreamException("ACP client closed before answering mixed request ids.");
			if (!response.TryGetProperty("id", out var id)
				|| !response.TryGetProperty("result", out var result)
				|| result.GetProperty("content").GetString() is not { } content) {
				throw new JsonException("ACP client returned a malformed mixed-id response.");
			}
			string key = id.ValueKind switch {
				JsonValueKind.Number => "number",
				JsonValueKind.String => "string",
				_ => throw new JsonException("ACP client changed a mixed request id type."),
			};
			if (!values.TryAdd(key, content)) throw new JsonException($"ACP client repeated the {key} response.");
		}
		Notify(sessionId, new JsonObject {
			["sessionUpdate"] = "agent_message_chunk",
			["messageId"] = "mixed-ids",
			["content"] = new JsonObject {
				["type"] = "text",
				["text"] = $"mixed ids: {values["number"]} | {values["string"]}",
			},
		});
		Respond(promptId, new JsonObject { ["stopReason"] = "end_turn" });
	}

	private static JsonObject Initialize() => new() {
		["protocolVersion"] = 1,
		["agentInfo"] = new JsonObject { ["name"] = "mixed-request-id-fake", ["version"] = "1" },
		["agentCapabilities"] = new JsonObject {
			["loadSession"] = true,
			["promptCapabilities"] = new JsonObject { ["image"] = true, ["embeddedContext"] = true },
			["sessionCapabilities"] = new JsonObject {
				["resume"] = new JsonObject(),
				["close"] = new JsonObject(),
			},
			["mcpCapabilities"] = new JsonObject { ["http"] = true, ["sse"] = false },
		},
		["authMethods"] = new JsonArray(),
		["_meta"] = new JsonObject { ["steering"] = new JsonObject { ["supported"] = true } },
	};

	private static JsonObject Setup(string sessionId) => new() {
		["sessionId"] = sessionId,
		["configOptions"] = new JsonArray(new JsonObject {
			["id"] = "model",
			["name"] = "Model",
			["category"] = "model",
			["type"] = "select",
			["currentValue"] = "mixed",
			["options"] = new JsonArray(new JsonObject {
				["value"] = "mixed",
				["name"] = "Mixed IDs",
				["description"] = "Exercises numeric and string JSON-RPC ids.",
			}),
		}),
		["modes"] = new JsonObject {
			["currentModeId"] = "default",
			["availableModes"] = new JsonArray(new JsonObject {
				["id"] = "default",
				["name"] = "Default",
				["description"] = "Default mode.",
			}),
		},
	};

	private static void Request(JsonNode id, string sessionId, string path) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = id,
		["method"] = "fs/read_text_file",
		["params"] = new JsonObject {
			["sessionId"] = sessionId,
			["path"] = path,
		},
	});

	private static void Notify(string sessionId, JsonObject update) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["method"] = "session/update",
		["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update },
	});

	private static void Respond(JsonElement id, JsonNode result) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = JsonNode.Parse(id.GetRawText()),
		["result"] = result,
	});

	private static async Task<JsonElement?> ReadAsync() {
		string? line = await Console.In.ReadLineAsync().ConfigureAwait(false);
		if (line is null) return null;
		using var document = JsonDocument.Parse(line);
		return document.RootElement.Clone();
	}

	private static void Write(JsonNode value) {
		Console.Out.WriteLine(value.ToJsonString());
		Console.Out.Flush();
	}
}
