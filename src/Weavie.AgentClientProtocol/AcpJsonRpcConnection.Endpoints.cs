using System.Text.Json;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpJsonRpcConnection {
	private readonly Lock _endpointGate = new();
	private readonly List<AcpSessionEndpoint> _endpoints = [];
	private readonly Dictionary<(long Generation, string Id), AcpSessionEndpoint> _incomingOwners = [];

	private bool HasEndpoints(long generation) {
		lock (_endpointGate) return _endpoints.Any(endpoint => endpoint.Generation == generation);
	}

	private void RetireEndpoints() {
		AcpSessionEndpoint[] endpoints;
		lock (_endpointGate) {
			endpoints = [.. _endpoints];
			_endpoints.Clear();
			_incomingOwners.Clear();
		}
		foreach (var endpoint in endpoints) endpoint.Retire();
	}

	internal AcpSessionEndpoint OpenEndpoint(long generation, string? sessionId,
		Action<long, JsonElement> notification, Action<AcpClientRequest> request) {
		var endpoint = new AcpSessionEndpoint(this, generation, notification, request);
		lock (_endpointGate) {
			if (sessionId is null && _endpoints.Any(value => value.Generation == generation && value.SessionId is null && !value.Retired)) {
				throw new InvalidOperationException("An ACP conversation is already opening.");
			}
			if (sessionId is not null) BindEndpoint(endpoint, sessionId);
			_endpoints.Add(endpoint);
		}
		return endpoint;
	}

	internal void BindEndpoint(AcpSessionEndpoint endpoint, string sessionId) {
		lock (_endpointGate) {
			if (_endpoints.Any(owner => owner != endpoint && owner.Generation == endpoint.Generation && owner.SessionId == sessionId)) {
				throw new AcpProtocolException($"ACP conversation '{sessionId}' already has an owner.");
			}
			endpoint.SetIdentity(sessionId);
		}
	}

	private AcpSessionEndpoint Endpoint(long generation, string sessionId) {
		lock (_endpointGate) {
			var endpoint = _endpoints.Find(value => value.Generation == generation && value.SessionId == sessionId);
			if (endpoint is not null) return endpoint;
			var opening = _endpoints.SingleOrDefault(value => value.Generation == generation && value.SessionId is null && !value.Retired)
				?? throw new AcpProtocolException($"ACP addressed an unknown conversation '{sessionId}'.");
			opening.Bind(sessionId);
			return opening;
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
