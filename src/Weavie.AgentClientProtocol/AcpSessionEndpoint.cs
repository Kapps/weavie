using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

internal sealed class AcpSessionEndpoint(AcpJsonRpcConnection connection, long generation) {
	private readonly Lock _gate = new();
	private readonly Queue<Action> _delivery = [];
	private Handlers? _handlers;
	private bool _draining;
	private bool _retired;
	private string? _sessionId;

	internal long Generation { get; } = generation;
	internal string SessionId => _sessionId ?? throw new AcpProtocolException("The ACP conversation has not opened.");

	internal void Bind(string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		lock (_gate) {
			if (_sessionId is not null && _sessionId != sessionId) {
				throw new AcpProtocolException("ACP changed the opening conversation's identity.");
			}
			connection.BindEndpoint(this, sessionId);
			_sessionId = sessionId;
		}
	}

	internal void Attach(Action<long, JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> failure) {
		lock (_gate) {
			if (_handlers is not null) throw new InvalidOperationException("The ACP endpoint already has an owner.");
			_handlers = new Handlers(notification, request, failure);
		}
		Drain();
	}

	internal void Retire() {
		lock (_gate) _retired = true;
		connection.CancelOpeningEndpoint(this);
		Drain();
	}

	internal Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) =>
		connection.RequestForEndpointAsync(method, Address(parameters), this, null, ct);

	internal Task NotifyAsync(string method, object parameters) =>
		connection.NotifyAsync(method, Address(parameters), Generation);

	internal Task<JsonElement> AuthenticateAsync(string methodId, CancellationToken ct) {
		EnsureActive();
		return connection.RequestForEndpointAsync("authenticate", new { methodId }, this, null, ct);
	}

	internal Task<JsonElement> CreateAsync(object parameters, CancellationToken ct) {
		EnsureActive();
		ct.ThrowIfCancellationRequested();
		return connection.RequestForEndpointAsync("session/new", Unaddressed(parameters), this, this, CancellationToken.None);
	}

	internal Task<AcpSessionEndpoint> ForkAsync(object parameters, CancellationToken ct) =>
		connection.ForkEndpointAsync(this, Address(parameters), ct);

	internal void EnsureActive() {
		lock (_gate) ObjectDisposedException.ThrowIf(_retired, this);
	}

	internal Task<JsonElement> CloseAsync() {
		var parameters = new { sessionId = SessionId };
		Retire();
		return connection.RequestForEndpointAsync("session/close", parameters, this, null, CancellationToken.None);
	}

	private JsonObject Address(object parameters) {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_retired, this);
			var addressed = Unaddressed(parameters);
			addressed.Add("sessionId", SessionId);
			return addressed;
		}
	}

	private static JsonObject Unaddressed(object parameters) {
		var value = JsonSerializer.SerializeToNode(parameters) as JsonObject
			?? throw new ArgumentException("ACP parameters must be an object.", nameof(parameters));
		if (value.ContainsKey("sessionId")) {
			throw new ArgumentException("The endpoint supplies its own sessionId.", nameof(parameters));
		}
		return value;
	}

	internal void Notify(JsonElement notification) {
		lock (_gate) if (_retired) return;
		Enqueue(() => {
			lock (_gate) if (_retired) return;
			_handlers!.Notification(Generation, notification);
		});
	}

	internal void Request(AcpClientRequest request) {
		lock (_gate) {
			if (_retired) {
				connection.RejectClosedRequest(request);
				return;
			}
		}
		Enqueue(() => {
			bool retired;
			lock (_gate) retired = _retired;
			if (retired) connection.RejectClosedRequest(request);
			else _handlers!.Request(request);
		});
	}

	private void Enqueue(Action action) {
		lock (_gate) _delivery.Enqueue(action);
		Drain();
	}

	private void Drain() {
		lock (_gate) {
			if (_draining || (_handlers is null && !_retired)) return;
			_draining = true;
		}
		while (true) {
			Action action;
			lock (_gate) {
				if (!_delivery.TryDequeue(out action!)) {
					_draining = false;
					return;
				}
			}
			try { action(); } catch (Exception error) { _handlers!.Failure(error); }
		}
	}

	private sealed record Handlers(
		Action<long, JsonElement> Notification, Action<AcpClientRequest> Request, Action<Exception> Failure);
}
