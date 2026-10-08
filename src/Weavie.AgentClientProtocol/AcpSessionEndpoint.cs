using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

// The process generation stays private to the endpoint and the connection, so a conversation cannot compare one.
internal sealed class AcpSessionEndpoint(
	AcpJsonRpcConnection connection, long generation,
	Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) {
	private volatile bool _retired;
	private volatile bool _opened;
	internal string? SessionId { get; private set; }
	internal bool Retired => _retired;
	// Until its conversation starts opening, the connection routes the generation's unscoped traffic as before any endpoint.
	internal bool Opened => _opened;
	internal void Open() => _opened = true;
	internal void Retire() => _retired = true;
	internal void Bind(string sessionId) => connection.BindEndpoint(this, generation, sessionId);

	internal void SetIdentity(string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		if (SessionId is not null && SessionId != sessionId) {
			throw new AcpProtocolException("ACP changed the opening conversation's identity.");
		}
		SessionId = sessionId;
	}

	internal Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct) =>
		connection.RequestForEndpointAsync(method, Address(parameters), this, generation, null, ct);
	internal Task NotifyAsync(string method, JsonObject parameters) =>
		connection.NotifyAsync(method, Address(parameters), generation);
	internal Task<JsonElement> AuthenticateAsync(string methodId, CancellationToken ct) =>
		connection.RequestForEndpointAsync("authenticate", Parameters(new JsonObject { ["methodId"] = methodId }), this, generation, null, ct);
	internal Task<JsonElement> CreateAsync(JsonObject parameters) =>
		connection.CreateForEndpointAsync("session/new", Parameters(parameters), this, generation);
	internal Task<JsonElement> ForkFromAsync(AcpSessionEndpoint parent, JsonObject parameters) {
		ObjectDisposedException.ThrowIf(_retired, this);
		return connection.CreateForEndpointAsync("session/fork", parent.Address(parameters), this, generation);
	}
	internal Task<JsonElement> CloseAsync() {
		Retire();
		return connection.RequestForEndpointAsync(
			"session/close", new JsonObject { ["sessionId"] = SessionId }, this, generation, null, CancellationToken.None);
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

	internal AcpSessionEndpoint OpenBranch(Action<JsonElement> observer) {
		var branch = connection.OpenEndpoint(generation, observer, connection.RejectClosedRequest, static _ => { });
		branch.Open();
		return branch;
	}
	internal bool ReportHealthy() => connection.ReportHealthy(generation);
	internal void Terminate(string reason) => connection.TerminateGeneration(generation, reason);

	// Responses answer the agent's own requests, so they still go out after retirement.
	internal Task RespondAsync(AcpClientRequest value, JsonNode? result) => connection.RespondAsync(value, result);
	internal Task RespondErrorAsync(AcpClientRequest value, int code, string message, JsonNode? data) =>
		connection.RespondErrorAsync(value, code, message, data);
	internal void Reject(AcpClientRequest value) => connection.RejectClosedRequest(value);

	internal void Notify(JsonElement value) {
		if (!_retired) notification(value);
	}
	internal void Request(AcpClientRequest value) {
		if (_retired) Reject(value);
		else request(value);
	}
	internal void Fault(Exception error) {
		if (!_retired) fault(error);
	}
}

/// <summary>One started process generation that conversations attach their endpoints to.</summary>
internal sealed class AcpProcess(AcpJsonRpcConnection connection, long generation) {
	internal AcpSessionEndpoint OpenEndpoint(
		Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) =>
		connection.OpenEndpoint(generation, notification, request, fault);
}
