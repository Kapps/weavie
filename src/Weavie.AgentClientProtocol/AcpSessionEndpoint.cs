using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

internal sealed class AcpSessionEndpoint(
	AcpJsonRpcConnection connection, long generation,
	Action<long, JsonElement> notification, Action<AcpClientRequest> request) {
	private volatile bool _retired;
	internal long Generation { get; } = generation;
	internal string? SessionId { get; private set; }
	internal bool Retired => _retired;
	internal void Retire() => _retired = true;
	internal void Bind(string sessionId) => connection.BindEndpoint(this, sessionId);

	internal void SetIdentity(string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		if (SessionId is not null && SessionId != sessionId) {
			throw new AcpProtocolException("ACP changed the opening conversation's identity.");
		}
		SessionId = sessionId;
	}

	internal Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) =>
		connection.RequestForEndpointAsync(method, Address(parameters), this, null, ct);
	internal Task NotifyAsync(string method, object parameters) =>
		connection.NotifyAsync(method, Address(parameters), Generation);
	internal Task<JsonElement> AuthenticateAsync(string methodId, CancellationToken ct) =>
		connection.RequestForEndpointAsync("authenticate", Parameters(new { methodId }), this, null, ct);
	internal Task<JsonElement> CreateAsync(object parameters) =>
		connection.RequestForEndpointAsync("session/new", Parameters(parameters), this, this, CancellationToken.None);
	internal Task<JsonElement> ForkFromAsync(AcpSessionEndpoint parent, object parameters) {
		ObjectDisposedException.ThrowIf(_retired, this);
		return connection.RequestForEndpointAsync("session/fork", parent.Address(parameters), this, this, CancellationToken.None);
	}
	internal Task<JsonElement> CloseAsync() {
		Retire();
		return connection.RequestForEndpointAsync("session/close", new { sessionId = SessionId }, this, null, CancellationToken.None);
	}

	private JsonObject Address(object parameters) {
		var value = Parameters(parameters);
		value.Add("sessionId", SessionId ?? throw new AcpProtocolException("The ACP conversation has not opened."));
		return value;
	}

	private JsonObject Parameters(object parameters) {
		ObjectDisposedException.ThrowIf(_retired, this);
		var value = JsonSerializer.SerializeToNode(parameters) as JsonObject
			?? throw new ArgumentException("ACP parameters must be an object.", nameof(parameters));
		if (value.ContainsKey("sessionId")) throw new ArgumentException("The endpoint supplies its own sessionId.", nameof(parameters));
		return value;
	}

	internal void Notify(JsonElement value) {
		if (!_retired) notification(Generation, value);
	}
	internal void Request(AcpClientRequest value) {
		if (_retired) connection.RejectClosedRequest(value);
		else request(value);
	}
}
