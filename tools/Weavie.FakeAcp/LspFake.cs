using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Lsp;

namespace Weavie.FakeAcp;

internal static class LspFake {
	private const string RegistrationId = "dynamic-registration";
	private const string ReadyFile = ".fake-lsp-ready";

	public static async Task RunAsync() {
		var input = Console.OpenStandardInput();
		var output = Console.OpenStandardOutput();
		var opened = new HashSet<string>(StringComparer.Ordinal);
		while (await LspFraming.ReadFrameAsync(input, CancellationToken.None).ConfigureAwait(false) is { } frame) {
			using var document = JsonDocument.Parse(frame);
			var message = document.RootElement;
			if (!message.TryGetProperty("method", out var methodValue)) {
				if (message.TryGetProperty("id", out var responseId)
					&& responseId.ValueKind == JsonValueKind.String
					&& responseId.GetString() == RegistrationId) {
					await File.WriteAllTextAsync(ReadyFile, string.Empty).ConfigureAwait(false);
				}
				continue;
			}

			string method = methodValue.GetString() ?? throw new JsonException("LSP method cannot be null.");
			switch (method) {
				case "initialize":
					await RespondAsync(output, message.GetProperty("id"), new JsonObject {
						["capabilities"] = new JsonObject(),
					}).ConfigureAwait(false);
					break;
				case "initialized":
					await RegisterAsync(output).ConfigureAwait(false);
					break;
				case "textDocument/didOpen":
					opened.Add(message.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString()
						?? throw new JsonException("didOpen URI cannot be null."));
					break;
				case "textDocument/definition":
					await DefineAsync(output, message, opened).ConfigureAwait(false);
					break;
			}
		}
	}

	private static Task RegisterAsync(Stream output) => WriteAsync(output, new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = RegistrationId,
		["method"] = "client/registerCapability",
		["params"] = new JsonObject {
			["registrations"] = new JsonArray {
				Registration("textDocument/definition"),
				Registration("textDocument/didOpen"),
			},
		},
	});

	private static JsonObject Registration(string method) => new() {
		["id"] = method,
		["method"] = method,
		["registerOptions"] = new JsonObject {
			["documentSelector"] = new JsonArray {
				new JsonObject {
					["language"] = "csharp",
					["scheme"] = "file",
					["pattern"] = "**/*.cs",
				},
			},
		},
	};

	private static async Task DefineAsync(
		Stream output,
		JsonElement message,
		IReadOnlySet<string> opened) {
		var id = message.GetProperty("id");
		string uri = message.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString()
			?? throw new JsonException("definition URI cannot be null.");
		if (!opened.Contains(uri)) {
			await ErrorAsync(output, id, "definition requested before didOpen").ConfigureAwait(false);
			return;
		}

		int slash = uri.LastIndexOf('/');
		string target = uri[..(slash + 1)] + "Widget.cs";
		await RespondAsync(output, id, new JsonArray {
			new JsonObject {
				["uri"] = target,
				["range"] = new JsonObject {
					["start"] = new JsonObject { ["line"] = 0, ["character"] = 20 },
					["end"] = new JsonObject { ["line"] = 0, ["character"] = 26 },
				},
			},
		}).ConfigureAwait(false);
	}

	private static Task RespondAsync(Stream output, JsonElement id, JsonNode result) =>
		WriteAsync(output, new JsonObject {
			["jsonrpc"] = "2.0",
			["id"] = JsonNode.Parse(id.GetRawText()),
			["result"] = result,
		});

	private static Task ErrorAsync(Stream output, JsonElement id, string message) =>
		WriteAsync(output, new JsonObject {
			["jsonrpc"] = "2.0",
			["id"] = JsonNode.Parse(id.GetRawText()),
			["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
		});

	private static Task WriteAsync(Stream output, JsonNode message) =>
		LspFraming.WriteFrameAsync(
			output,
			JsonSerializer.SerializeToUtf8Bytes(message),
			CancellationToken.None);
}
