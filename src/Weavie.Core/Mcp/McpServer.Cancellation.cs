using System.Net.Sockets;
using System.Text.Json;
using Weavie.Core.Sessions;
using Weavie.Workflows;

namespace Weavie.Core.Mcp;

// Per-call cancellation: a tool call stops when its client cancels it, hangs up, or the server is disposed.
public sealed partial class McpServer {
	private readonly Dictionary<(CapabilityRegistryGrant Grant, object Client, string Id), CancellationTokenSource> _calls = [];
	private readonly Lock _callsGate = new();

	private InflightCall BeginCall(CapabilityRegistryGrant grant, object client, string? idRaw, CancellationToken ct) {
		if (!grant.Live) throw new ToolUnavailableException("This conversation has ended.");
		var stage = grant.CaptureStage();
		var source = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Lifetime);
		if (idRaw is not null) {
			lock (_callsGate) _calls[(grant, client, idRaw)] = source;
		}
		return new InflightCall(this, (grant, client, idRaw ?? string.Empty), source, stage);
	}

	private void CancelCall(CapabilityRegistryGrant grant, object client, JsonElement notification) {
		if (!notification.TryGetProperty("params", out var parameters)
			|| !parameters.TryGetProperty("requestId", out var requestId)) {
			return;
		}
		lock (_callsGate) {
			if (_calls.TryGetValue((grant, client, requestId.GetRawText()), out var source)) source.Cancel();
		}
	}

	// An HTTP client sends nothing after its request, so a read completing or failing means it hung up.
	private static async Task CancelOnDisconnectAsync(NetworkStream stream, CancellationTokenSource connection) {
		try {
			await stream.ReadAsync(new byte[1], connection.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			return;
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
		}
		await connection.CancelAsync().ConfigureAwait(false);
	}

	private sealed class InflightCall(McpServer server, (CapabilityRegistryGrant, object, string) key, CancellationTokenSource source, WorkflowStageCall? stage) : IDisposable {
		private TaskCompletionSource? _response;
		public CancellationToken Token => source.Token;
		public WorkflowStageCall? Stage => stage;
		public ISessionPeers Sessions => key.Item1.Sessions;
		public void BeginCompletionResponse() => _response = key.Item1.BeginCompletionResponse();

		public void Dispose() {
			if (_response is { } response) key.Item1.EndCompletionResponse(response);
			lock (server._callsGate) {
				if (server._calls.TryGetValue(key, out var current) && current == source) server._calls.Remove(key);
				source.Dispose();
			}
		}
	}
}
