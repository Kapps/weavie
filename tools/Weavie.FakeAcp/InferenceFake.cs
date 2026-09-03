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
		string model = "fake-model";
		string effort = "medium";
		bool fast = false;
		bool booleanConfigOptions = false;
		var mutations = new List<string>();
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
					booleanConfigOptions = SupportsBooleanConfigOptions(root);
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
						["configOptions"] = Configuration(
							model, effort, fast, variant, booleanConfigOptions),
					});
					break;
				case "session/set_config_option":
					var parameters = root.GetProperty("params");
					string configId = parameters.GetProperty("configId").GetString() ?? string.Empty;
					if (configId == "model") model = parameters.GetProperty("value").GetString() ?? string.Empty;
					else if (configId == "effort") effort = parameters.GetProperty("value").GetString() ?? string.Empty;
					else if (configId is "fast" or "fast-mode") {
						var value = parameters.GetProperty("value");
						fast = value.ValueKind == JsonValueKind.True
							|| value.ValueKind == JsonValueKind.String && value.GetString() == "on";
					}
					mutations.Add(configId);
					Respond(id, new JsonObject {
						["configOptions"] = Configuration(
							model, effort, fast, variant, booleanConfigOptions),
					});
					break;
				case "session/prompt":
					foreach (var block in root.GetProperty("params").GetProperty("prompt").EnumerateArray()) {
						if (AcpJson.OptionalString(block, "type") != "image") continue;
						imageMime = AcpJson.OptionalString(block, "mimeType") ?? string.Empty;
						imageData = AcpJson.OptionalString(block, "data") ?? string.Empty;
					}
					Chunk(Reply(
						variant,
						cwd,
						refusedProbe,
						imageMime,
						imageData,
						model,
						effort,
						fast,
						booleanConfigOptions,
						mutations));
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
		string imageData,
		string model,
		string effort,
		bool fast,
		bool booleanConfigOptions,
		IReadOnlyList<string> mutations) => variant switch {
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
				["model"] = model,
				["effort"] = effort,
				["fast"] = fast,
				["booleanConfigOptions"] = booleanConfigOptions,
				["mutations"] = new JsonArray(mutations.Select(mutation => JsonValue.Create(mutation)).ToArray()),
			}),
		};

	private static JsonArray Configuration(
		string model,
		string effort,
		bool fast,
		string variant,
		bool booleanConfigOptions) {
		var options = new JsonArray {
			new JsonObject {
				["id"] = "model",
				["name"] = "Model",
				["category"] = "model",
				["type"] = "select",
				["currentValue"] = model,
				["options"] = new JsonArray(
					new JsonObject {
						["group"] = "available",
						["name"] = "Available",
						["options"] = new JsonArray(
							new JsonObject { ["value"] = "fake-model", ["name"] = "Fake Model" },
							new JsonObject { ["value"] = "opus", ["name"] = "Opus" }),
					}),
			},
			new JsonObject {
				["id"] = "effort",
				["name"] = "Effort",
				["category"] = "thought_level",
				["type"] = "select",
				["currentValue"] = effort,
				["options"] = model == "opus"
					? new JsonArray(
						new JsonObject { ["value"] = "medium", ["name"] = "Medium" },
						new JsonObject { ["value"] = "low", ["name"] = "Low" })
					: new JsonArray(new JsonObject { ["value"] = "medium", ["name"] = "Medium" }),
			},
		};
		if (variant != "no-fast") {
			var fastOption = new JsonObject {
				["id"] = variant == "select-fast-mode" ? "fast-mode" : "fast",
				["name"] = "Fast",
				["category"] = "model_config",
			};
			if (booleanConfigOptions && variant != "select-fast-mode") {
				fastOption["type"] = "boolean";
				fastOption["currentValue"] = fast;
			} else {
				fastOption["type"] = "select";
				fastOption["currentValue"] = fast ? "on" : "off";
				fastOption["options"] = new JsonArray(
					new JsonObject { ["value"] = "on", ["name"] = "On" },
					new JsonObject { ["value"] = "off", ["name"] = "Off" });
			}
			options.Add(fastOption);
		}
		return options;
	}

	private static bool SupportsBooleanConfigOptions(JsonElement request) =>
		request.GetProperty("params").GetProperty("clientCapabilities")
			.TryGetProperty("session", out var session)
		&& session.TryGetProperty("configOptions", out var configOptions)
		&& configOptions.TryGetProperty("boolean", out _);

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
