using System.Net.WebSockets;

namespace Weavie.Core.Mcp;

public sealed partial class McpServer {
	private interface IMcpResponder {
		Task SendResultAsync(string? idRaw, string resultJson, CancellationToken ct);

		Task SendErrorAsync(string? idRaw, int code, string messageText, CancellationToken ct);

		Task SendRawAsync(string json, CancellationToken ct);
	}

	private sealed class WebSocketResponder(McpServer server, WebSocket webSocket) : IMcpResponder {
		public Task SendResultAsync(string? idRaw, string resultJson, CancellationToken ct) {
			if (idRaw is null) {
				return Task.CompletedTask;
			}

			return server.SendRawAsync(webSocket, $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}", ct);
		}

		public Task SendErrorAsync(string? idRaw, int code, string messageText, CancellationToken ct) {
			string id = idRaw ?? "null";
			return server.SendRawAsync(webSocket, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":{JsonString(messageText)}}}}}", ct);
		}

		public Task SendRawAsync(string json, CancellationToken ct) => server.SendRawAsync(webSocket, json, ct);
	}

	private sealed class HttpResponder : IMcpResponder {
		public string? ResponseJson { get; private set; }

		public Task SendResultAsync(string? idRaw, string resultJson, CancellationToken ct) {
			if (idRaw is not null) {
				ResponseJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}";
			}

			return Task.CompletedTask;
		}

		public Task SendErrorAsync(string? idRaw, int code, string messageText, CancellationToken ct) {
			string id = idRaw ?? "null";
			ResponseJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":{JsonString(messageText)}}}}}";
			return Task.CompletedTask;
		}

		public Task SendRawAsync(string json, CancellationToken ct) {
			ResponseJson = json;
			return Task.CompletedTask;
		}
	}
}
