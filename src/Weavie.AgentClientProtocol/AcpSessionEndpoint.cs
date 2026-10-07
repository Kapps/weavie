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

	internal Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct) =>
		connection.RequestForEndpointAsync(method, Address(parameters), this, null, ct);
	internal Task NotifyAsync(string method, JsonObject parameters) =>
		connection.NotifyAsync(method, Address(parameters), Generation);
	internal Task<JsonElement> AuthenticateAsync(string methodId, CancellationToken ct) =>
		connection.RequestForEndpointAsync("authenticate", Parameters(new JsonObject { ["methodId"] = methodId }), this, null, ct);
	internal Task<JsonElement> CreateAsync(JsonObject parameters) =>
		connection.CreateForEndpointAsync("session/new", Parameters(parameters), this);
	internal Task<JsonElement> ForkFromAsync(AcpSessionEndpoint parent, JsonObject parameters) {
		ObjectDisposedException.ThrowIf(_retired, this);
		return connection.CreateForEndpointAsync("session/fork", parent.Address(parameters), this);
	}
	internal Task<JsonElement> CloseAsync() {
		Retire();
		return connection.RequestForEndpointAsync("session/close", new JsonObject { ["sessionId"] = SessionId }, this, null, CancellationToken.None);
	}

	private JsonObject Address(JsonObject parameters) {
		var value = Parameters(parameters);
		value.Add("sessionId", SessionId ?? throw new AcpProtocolException("The ACP conversation has not opened."));
		return value;
	}

	private JsonObject Parameters(JsonObject parameters) {
		ObjectDisposedException.ThrowIf(_retired, this);
		if (parameters.ContainsKey("sessionId")) throw new ArgumentException("The endpoint supplies its own sessionId.", nameof(parameters));
		return parameters;
	}

	internal void Notify(JsonElement value) {
		if (!_retired) notification(Generation, value);
	}
	internal void Request(AcpClientRequest value) {
		if (_retired) connection.RejectClosedRequest(value);
		else request(value);
	}
}
