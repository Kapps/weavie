using System.Net.WebSockets;

namespace Weavie.Core.Mcp;

public sealed partial class McpServer {
	internal interface IMcpResponder {
		// Scopes request ids: notifications/cancelled only reaches calls from the same client.
		object Client { get; }

		Task SendResultAsync(string? idRaw, string resultJson, CancellationToken ct);

		Task SendErrorAsync(string? idRaw, int code, string messageText, CancellationToken ct);
	}

	private static string ResultJson(string idRaw, string resultJson) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}";

	private static string ErrorJson(string? idRaw, int code, string messageText) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw ?? "null"},\"error\":{{\"code\":{code},\"message\":{JsonString(messageText)}}}}}";

	private static string ProgressJson(string progressToken, int progress) =>
		$"{{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{{\"progressToken\":{progressToken},\"progress\":{progress}}}}}";

	private sealed class WebSocketResponder(McpServer server, WebSocket webSocket) : IMcpResponder {
		public object Client => this;

		public Task SendResultAsync(string? idRaw, string resultJson, CancellationToken ct) =>
			idRaw is null ? Task.CompletedTask : server.SendRawAsync(webSocket, ResultJson(idRaw, resultJson), ct);

		public Task SendErrorAsync(string? idRaw, int code, string messageText, CancellationToken ct) =>
			server.SendRawAsync(webSocket, ErrorJson(idRaw, code, messageText), ct);
	}
}
