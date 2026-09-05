using System.Text.Json;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpJsonRpcConnection {
	private readonly Lock _endpointGate = new();
	private readonly SemaphoreSlim _forkGate = new(1, 1);
	private readonly Dictionary<(long Generation, string Id), AcpSessionEndpoint> _endpoints = [];
	private readonly Dictionary<(long Generation, string Id), AcpSessionEndpoint> _incomingOwners = [];
	private readonly Dictionary<long, AcpSessionEndpoint> _openingEndpoints = [];

	internal async Task<AcpSessionEndpoint> ForkEndpointAsync(AcpSessionEndpoint parent, object parameters, CancellationToken ct) {
		await _forkGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			parent.EnsureActive();
			var child = OpenEndpoint(parent.Generation);
			try {
				// A sent creation owns its opening slot until the provider returns its identity or fails.
				await RequestForEndpointAsync("session/fork", parameters, parent, child, CancellationToken.None).ConfigureAwait(false);
				return child;
			} catch {
				child.Retire();
				throw;
			}
		} finally {
			_forkGate.Release();
		}
	}

	private bool HasEndpoints(long generation) {
		lock (_endpointGate) return _openingEndpoints.ContainsKey(generation)
			|| _endpoints.Keys.Any(key => key.Generation == generation);
	}

	private void RetireEndpoints() {
		AcpSessionEndpoint[] endpoints;
		lock (_endpointGate) {
			endpoints = [.. _endpoints.Values.Concat(_openingEndpoints.Values).Distinct()];
			_endpoints.Clear();
			_openingEndpoints.Clear();
			_incomingOwners.Clear();
		}
		foreach (var endpoint in endpoints) endpoint.Retire();
	}

	internal AcpSessionEndpoint OpenEndpoint(long generation) {
		var endpoint = new AcpSessionEndpoint(this, generation);
		lock (_endpointGate) {
			if (!_openingEndpoints.TryAdd(generation, endpoint)) {
				throw new InvalidOperationException("An ACP conversation is already opening.");
			}
		}
		return endpoint;
	}

	internal AcpSessionEndpoint OpenEndpoint(long generation, string sessionId) {
		var endpoint = new AcpSessionEndpoint(this, generation);
		endpoint.Bind(sessionId);
		return endpoint;
	}

	internal void BindEndpoint(AcpSessionEndpoint endpoint, string sessionId) {
		lock (_endpointGate) {
			var key = (endpoint.Generation, sessionId);
			if (_endpoints.TryGetValue(key, out var owner) && !ReferenceEquals(owner, endpoint)) {
				throw new AcpProtocolException($"ACP conversation '{sessionId}' already has an owner.");
			}
			_endpoints[key] = endpoint;
			CancelOpeningEndpoint(endpoint);
		}
	}

	internal void CancelOpeningEndpoint(AcpSessionEndpoint endpoint) {
		lock (_endpointGate) {
			if (_openingEndpoints.GetValueOrDefault(endpoint.Generation) == endpoint) {
				_openingEndpoints.Remove(endpoint.Generation);
			}
		}
	}

	private AcpSessionEndpoint Endpoint(long generation, string sessionId) {
		AcpSessionEndpoint opening;
		lock (_endpointGate) {
			if (_endpoints.TryGetValue((generation, sessionId), out var endpoint)) return endpoint;
			if (!_openingEndpoints.TryGetValue(generation, out opening!)) {
				throw new AcpProtocolException($"ACP addressed an unknown conversation '{sessionId}'.");
			}
		}
		opening.Bind(sessionId);
		return opening;
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
