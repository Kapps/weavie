using System.Net.Sockets;
using System.Text.Json;

namespace Weavie.Core.Mcp;

// Per-call cancellation: a tool call stops when its client cancels it, hangs up, or the server is disposed.
public sealed partial class McpServer {
	private readonly Dictionary<(object Client, string Id), CancellationTokenSource> _calls = [];
	private readonly Lock _callsGate = new();

	private InflightCall BeginCall(object client, string? idRaw, CancellationToken ct) {
		var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
		if (idRaw is not null) {
			lock (_callsGate) _calls[(client, idRaw)] = source;
		}
		return new InflightCall(this, (client, idRaw ?? string.Empty), source);
	}

	private void CancelCall(object client, JsonElement notification) {
		if (!notification.TryGetProperty("params", out var parameters)
			|| !parameters.TryGetProperty("requestId", out var requestId)) {
			return;
		}
		lock (_callsGate) {
			if (_calls.TryGetValue((client, requestId.GetRawText()), out var source)) source.Cancel();
		}
	}

	// An HTTP client sends nothing after its request, so any read completing means it hung up.
	private static async Task CancelOnDisconnectAsync(NetworkStream stream, CancellationTokenSource connection) {
		try {
			await stream.ReadAsync(new byte[1], connection.Token).ConfigureAwait(false);
		} catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) {
			return;
		}
		await connection.CancelAsync().ConfigureAwait(false);
	}

	private sealed class InflightCall(McpServer server, (object, string) key, CancellationTokenSource source) : IDisposable {
		public CancellationToken Token => source.Token;

		public void Dispose() {
			lock (server._callsGate) {
				if (server._calls.TryGetValue(key, out var current) && current == source) server._calls.Remove(key);
				source.Dispose();
			}
		}
	}
}
