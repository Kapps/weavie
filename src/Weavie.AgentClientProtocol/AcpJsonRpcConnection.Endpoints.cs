using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpJsonRpcConnection {
	private readonly Lock _endpointGate = new();
	private readonly SemaphoreSlim _openingGate = new(1, 1);
	private (long Generation, AcpSessionEndpoint Endpoint)? _openingOwner;
	private readonly List<(long Generation, AcpSessionEndpoint Endpoint)> _endpoints = [];
	private readonly Dictionary<(long Generation, string Id), AcpSessionEndpoint> _incomingOwners = [];

	private bool HasEndpoints(long generation) {
		lock (_endpointGate) return _endpoints.Any(entry => entry.Generation == generation && entry.Endpoint.Opened);
	}

	private void RetireEndpoints() {
		AcpSessionEndpoint[] endpoints;
		lock (_endpointGate) {
			endpoints = [.. _endpoints.Select(entry => entry.Endpoint)];
			_endpoints.Clear();
			_incomingOwners.Clear();
		}
		foreach (var endpoint in endpoints) endpoint.Retire();
	}

	internal AcpSessionEndpoint OpenEndpoint(long generation,
		Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) {
		var endpoint = new AcpSessionEndpoint(this, generation, notification, request, fault);
		lock (_endpointGate) _endpoints.Add((generation, endpoint));
		return endpoint;
	}

	internal AcpSessionEndpoint OpenChildEndpoint(long generation, string sessionId,
		Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) {
		var endpoint = new AcpSessionEndpoint(this, generation, notification, request, fault);
		lock (_endpointGate) {
			BindLocked(endpoint, generation, sessionId);
			endpoint.Open();
			_endpoints.Add((generation, endpoint));
		}
		return endpoint;
	}

	internal void SinkEndpoint(long generation, string sessionId) {
		lock (_endpointGate) {
			if (_endpoints.Any(owner => owner.Generation == generation && owner.Endpoint.SessionId == sessionId)) return;
			var sink = new AcpSessionEndpoint(this, generation, static _ => { }, static _ => { }, static _ => { });
			sink.SetIdentity(sessionId);
			sink.Retire();
			_endpoints.Add((generation, sink));
		}
	}

	private void FaultEndpoints(long generation, Exception error) {
		AcpSessionEndpoint[] endpoints;
		lock (_endpointGate) endpoints = [.. _endpoints.Where(entry => entry.Generation == generation).Select(entry => entry.Endpoint)];
		foreach (var endpoint in endpoints) endpoint.Fault(error);
	}

	internal async Task<JsonElement> CreateForEndpointAsync(
		string method, JsonObject parameters, AcpSessionEndpoint endpoint, long generation) {
		// ACP may send session traffic before returning its identity; the opening request owns that traffic.
		await _openingGate.WaitAsync().ConfigureAwait(false);
		try {
			lock (_endpointGate) {
				ObjectDisposedException.ThrowIf(endpoint.Retired, endpoint);
				_openingOwner = (generation, endpoint);
			}
			return await RequestForEndpointAsync(
				method, parameters, endpoint, generation, endpoint, CancellationToken.None).ConfigureAwait(false);
		} finally {
			lock (_endpointGate) _openingOwner = null;
			_openingGate.Release();
		}
	}

	internal async Task CloseSessionAsync(AcpSessionEndpoint endpoint, long generation, Task<bool> opening) {
		if (!await opening.ConfigureAwait(false) || endpoint.SessionId is not { } sessionId) return;
		try {
			await RequestForEndpointAsync("session/close", new JsonObject { ["sessionId"] = sessionId },
				endpoint, generation, null, CancellationToken.None).ConfigureAwait(false);
		} catch (AcpRequestException error) {
			// Only closing stops a session on a process that keeps running, so a refusal stops the process.
			TerminateGeneration(generation, $"{_providerName} could not close a replaced conversation: {error.Message}");
		} catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) {
			_log($"[acp:{_providerId}] session/close for {sessionId} ended with its process: {error.Message}");
		}
	}

	internal void BindEndpoint(AcpSessionEndpoint endpoint, long generation, string sessionId) {
		lock (_endpointGate) BindLocked(endpoint, generation, sessionId);
	}

	private void BindLocked(AcpSessionEndpoint endpoint, long generation, string sessionId) {
		if (_endpoints.Any(owner => owner.Endpoint != endpoint && owner.Generation == generation && owner.Endpoint.SessionId == sessionId)) {
			throw new AcpProtocolException($"ACP conversation '{sessionId}' already has an owner.");
		}
		endpoint.SetIdentity(sessionId);
	}

	private AcpSessionEndpoint Endpoint(long generation, string sessionId) {
		lock (_endpointGate) {
			var endpoint = _endpoints.Find(entry => entry.Generation == generation && entry.Endpoint.SessionId == sessionId).Endpoint;
			if (endpoint is not null) return endpoint;
			if (_openingOwner is not { } opening || opening.Generation != generation || opening.Endpoint.SessionId is not null) {
				throw new AcpProtocolException($"ACP addressed an unknown conversation '{sessionId}'.");
			}
			opening.Endpoint.Bind(sessionId);
			return opening.Endpoint;
		}
	}

	private void DispatchNotification(long generation, JsonElement notification) {
		if (!HasEndpoints(generation)) {
			NotificationReceived?.Invoke(generation, notification);
			return;
		}
		if (notification.TryGetProperty("params", out var parameters)) {
			if (parameters.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == JsonValueKind.String) {
				Endpoint(generation, sessionId.GetString()!).Notify(notification);
				return;
			}
			if (notification.GetProperty("method").GetString() == "$/cancel_request"
				&& parameters.TryGetProperty("requestId", out var requestId)) {
				AcpSessionEndpoint? owner;
				lock (_endpointGate) _incomingOwners.TryGetValue((generation, CanonicalId(requestId)), out owner);
				if (owner is not null) {
					_log($"[acp:{_providerId}] agent cancelled request {CanonicalId(requestId)}");
					owner.Notify(notification);
					return;
				}
			}
		}
		NotificationReceived?.Invoke(generation, notification);
	}

	private void DispatchRequest(AcpClientRequest request) {
		if (!HasEndpoints(request.Generation)) {
			RequestReceived?.Invoke(request);
			return;
		}
		AcpSessionEndpoint? owner;
		if (request.Parameters.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == JsonValueKind.String) {
			owner = Endpoint(request.Generation, sessionId.GetString()!);
		} else if (request.Parameters.TryGetProperty("requestId", out var requestId)
			&& requestId.ValueKind == JsonValueKind.Number && requestId.TryGetInt64(out long id)
			&& _pending.TryGetValue(id, out var pending)) {
			owner = pending.Owner;
		} else {
			throw new AcpProtocolException($"ACP request '{request.Method}' has no known conversation owner.");
		}
		if (owner is null) {
			RequestReceived?.Invoke(request);
			return;
		}
		lock (_endpointGate) {
			if (!_incomingOwners.TryAdd((request.Generation, request.Id), owner)) {
				throw new AcpProtocolException($"ACP request '{request.Id}' is already active.");
			}
		}
		owner.Request(request);
	}

	internal void RejectClosedRequest(AcpClientRequest request) => _ = RejectClosedRequestAsync(request);

	private async Task RejectClosedRequestAsync(AcpClientRequest request) {
		try {
			await RespondErrorAsync(request, -32602, "The conversation is closed.", null).ConfigureAwait(false);
		} catch (Exception error) {
			SignalProtocolFault(request.Generation, error, reportUnhealthy: true);
		}
	}
}
