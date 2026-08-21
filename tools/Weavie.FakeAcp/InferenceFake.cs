using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>
/// A minimal ACP agent that serves exactly one inference turn. It probes the client with an <c>fs/read_text_file</c>
/// request first, so a test can prove Weavie refuses it, and echoes the received <c>cwd</c> so a test can prove the
/// query ran in the owning worktree.
/// </summary>
internal static class InferenceFake {
	public static async Task RunAsync(string variant) {
		bool refusedProbe = false;
		string cwd = string.Empty;
		string imageMime = string.Empty;
		string imageData = string.Empty;
		while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line) {
			if (line.Length == 0) continue;
			using var document = JsonDocument.Parse(line);
			var root = document.RootElement;
			if (!root.TryGetProperty("method", out var methodElement)) {
				if (root.TryGetProperty("error", out _)) refusedProbe = true;
				continue;
			}

			string method = methodElement.GetString() ?? string.Empty;
			var id = root.TryGetProperty("id", out var raw) ? JsonNode.Parse(raw.GetRawText()) : null;
			switch (method) {
				case "initialize":
					var initialized = new JsonObject { ["protocolVersion"] = 1 };
					if (variant != "no-image-capability") {
						initialized["agentCapabilities"] = new JsonObject {
							["promptCapabilities"] = new JsonObject { ["image"] = true },
						};
					}
					Respond(id, initialized);
					break;
				case "session/new":
					cwd = root.GetProperty("params").GetProperty("cwd").GetString() ?? string.Empty;
					Probe();
					Respond(id, new JsonObject {
						["sessionId"] = "inference-session",
						["configOptions"] = new JsonArray {
							new JsonObject {
								["id"] = "model",
								["name"] = "Model",
								["category"] = "model",
								["type"] = "select",
								["currentValue"] = "fake-model",
								["options"] = new JsonArray(),
							},
						},
					});
					break;
				case "session/prompt":
					foreach (var block in root.GetProperty("params").GetProperty("prompt").EnumerateArray()) {
						if (AcpJson.OptionalString(block, "type") != "image") continue;
						imageMime = AcpJson.OptionalString(block, "mimeType") ?? string.Empty;
						imageData = AcpJson.OptionalString(block, "data") ?? string.Empty;
					}
					Chunk(Reply(variant, cwd, refusedProbe, imageMime, imageData));
					Respond(id, new JsonObject {
						["stopReason"] = variant == "refusal" ? "refusal" : "end_turn",
						["usage"] = new JsonObject {
							["inputTokens"] = 2,
							["outputTokens"] = 26,
							["cachedReadTokens"] = 4096,
							["cachedWriteTokens"] = 0,
						},
					});
					break;
				case "session/close":
					Respond(id, []);
					return;
				default:
					Respond(id, []);
					break;
			}
		}
	}

	private static string Reply(
		string variant,
		string cwd,
		bool refusedProbe,
		string imageMime,
		string imageData) => variant switch {
			"prose" => "Here you go!\n\n```json\n{\"branch\":\"feat/fenced\"}\n```",
			"empty" => string.Empty,
			// Valid JSON that is far past any sane output bound, to prove the client stops accumulating.
			"oversize" => "{\"branch\":\"" + new string('x', 200_000) + "\"}",
			"refusal" => string.Empty,
			"image" => JsonSerializer.Serialize(new JsonObject {
				["branch"] = "feat/fake-branch",
				["imageMime"] = imageMime,
				["imageData"] = imageData,
			}),
			_ => JsonSerializer.Serialize(new JsonObject {
				["branch"] = "feat/fake-branch",
				["cwd"] = cwd,
				["refusedProbe"] = refusedProbe,
			}),
		};

	private static void Probe() => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = 9001,
		["method"] = "fs/read_text_file",
		["params"] = new JsonObject { ["path"] = "/etc/hostname" },
	});

	private static void Chunk(string text) {
		if (text.Length == 0) return;
		Write(new JsonObject {
			["jsonrpc"] = "2.0",
			["method"] = "session/update",
			["params"] = new JsonObject {
				["sessionId"] = "inference-session",
				["update"] = new JsonObject {
					["sessionUpdate"] = "agent_message_chunk",
					["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
				},
			},
		});
	}

	private static void Respond(JsonNode? id, JsonObject result) =>
		Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

	private static void Write(JsonObject payload) {
		Console.Out.WriteLine(payload.ToJsonString());
		Console.Out.Flush();
	}
}
