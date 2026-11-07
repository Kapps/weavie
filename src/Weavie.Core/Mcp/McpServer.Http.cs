using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Mcp;

// Streamable HTTP: a request is answered with an event stream opened at once, so a client that times out
// waiting for response headers never gives up on a long tool call; a notification is answered 202.
public sealed partial class McpServer {
	/// <summary>How often a running tool call reports progress; clients' inactivity limits are minutes long.</summary>
	internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(30);

	private async Task HandleHttpClientAsync(NetworkStream stream, HttpRequestHead request, CancellationToken ct) {
		if (!IsAuthorized(request.Headers)) {
			Emit("rejected HTTP MCP request: missing/invalid auth token");
			await WebSocketHandshake.WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
			return;
		}

		if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) || !IsMcpTarget(request.Target)) {
			await WebSocketHandshake.WriteStatusAsync(stream, "404 Not Found", ct).ConfigureAwait(false);
			return;
		}

		string body = await WebSocketHandshake.ReadBodyAsync(stream, request.Headers, ct).ConfigureAwait(false);
		// A client without a session is issued one; it scopes that client's request ids for cancellation.
		string sessionId = request.Headers.TryGetValue("mcp-session-id", out string? presented) && presented.Length > 0
			? presented
			: Guid.NewGuid().ToString("N");
		using var responder = new HttpResponder(stream, sessionId, ProgressInterval);
		using (var connection = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
			var watch = CancelOnDisconnectAsync(stream, connection);
			try {
				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (IsRequest(root)) {
					await responder.OpenAsync(connection.Token).ConfigureAwait(false);
					if (ToolCallProgressToken(root) is { } token) responder.StartProgress(token, connection);
				} else if (root.ValueKind == JsonValueKind.Object) {
					responder.ExpectNoReply();
				}

				await DispatchAsync(responder, root, connection.Token).ConfigureAwait(false);
			} catch (JsonException ex) {
				await SendParseErrorAsync(responder, ex, connection.Token).ConfigureAwait(false);
			} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
				// The client cancelled the call or hung up; it expects no result either way.
			} finally {
				await connection.CancelAsync().ConfigureAwait(false);
				await responder.Progress.ConfigureAwait(false);
				await watch.ConfigureAwait(false);
			}
		}

		if (!responder.Opened) {
			await WebSocketHandshake.WriteStatusAsync(stream, "202 Accepted", ct).ConfigureAwait(false);
		}
	}

	private static bool IsRequest(JsonElement root) =>
		root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out _) && root.TryGetProperty("method", out _);

	private static string? ToolCallProgressToken(JsonElement root) =>
		root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.ValueEquals("tools/call")
		&& root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
		&& parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
		&& meta.TryGetProperty("progressToken", out var token) && token.ValueKind is JsonValueKind.String or JsonValueKind.Number
			? token.GetRawText()
			: null;

	private sealed class HttpResponder(NetworkStream stream, string sessionId, TimeSpan progressInterval) : IMcpResponder, IDisposable {
		private readonly SemaphoreSlim _gate = new(1, 1);
		private bool _answered;

		public object Client => sessionId;

		public bool Opened { get; private set; }

		// A notification never gets a reply, even when handling it fails.
		public void ExpectNoReply() => _answered = true;

		public async Task OpenAsync(CancellationToken ct) {
			await _gate.WaitAsync(ct).ConfigureAwait(false);
			try {
				await OpenLockedAsync(ct).ConfigureAwait(false);
			} finally {
				_gate.Release();
			}
		}

		public Task SendResultAsync(string? idRaw, string resultJson, CancellationToken ct) =>
			idRaw is null ? Task.CompletedTask : AnswerAsync(ResultJson(idRaw, resultJson), ct);

		public Task SendErrorAsync(string? idRaw, int code, string messageText, CancellationToken ct) =>
			AnswerAsync(ErrorJson(idRaw, code, messageText), ct);

		// Ends once the call is answered or the connection is cancelled; cancel the connection before awaiting it.
		public Task Progress { get; private set; } = Task.CompletedTask;

		public void StartProgress(string token, CancellationTokenSource connection) => Progress = ReportProgressAsync(token, connection);

		private async Task ReportProgressAsync(string token, CancellationTokenSource connection) {
			using var timer = new PeriodicTimer(progressInterval);
			int progress = 0;
			try {
				while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false)) {
					await _gate.WaitAsync(connection.Token).ConfigureAwait(false);
					try {
						if (_answered) return;
						await WriteEventAsync(ProgressJson(token, ++progress), connection.Token).ConfigureAwait(false);
					} finally {
						_gate.Release();
					}
				}
			} catch (OperationCanceledException) {
				// The call ended.
			} catch (IOException) {
				await connection.CancelAsync().ConfigureAwait(false);
			}
		}

		private async Task AnswerAsync(string json, CancellationToken ct) {
			await _gate.WaitAsync(ct).ConfigureAwait(false);
			try {
				if (_answered) return;
				_answered = true;
				await OpenLockedAsync(ct).ConfigureAwait(false);
				await WriteEventAsync(json, ct).ConfigureAwait(false);
			} finally {
				_gate.Release();
			}
		}

		// JSON strings can't hold a raw line break, so these are whitespace, and an SSE data line can't contain one.
		private async Task WriteEventAsync(string json, CancellationToken ct) {
			string line = json.Replace('\r', ' ').Replace('\n', ' ');
			await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: message\ndata: {line}\n\n"), ct).ConfigureAwait(false);
			await stream.FlushAsync(ct).ConfigureAwait(false);
		}

		private async Task OpenLockedAsync(CancellationToken ct) {
			if (Opened) return;
			Opened = true;
			await WebSocketHandshake.WriteEventStreamHeadAsync(stream, sessionId, ct).ConfigureAwait(false);
		}

		public void Dispose() => _gate.Dispose();
	}
}
