using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

internal sealed class AcpSessionEndpoint(
	AcpJsonRpcConnection connection, long generation,
	Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) {
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
		connection.CreateForEndpointAsync("session/new", Parameters(parameters), this);
	internal Task<JsonElement> ForkFromAsync(AcpSessionEndpoint parent, object parameters) {
		ObjectDisposedException.ThrowIf(_retired, this);
		return connection.CreateForEndpointAsync("session/fork", parent.Address(parameters), this);
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

	internal AcpSessionEndpoint OpenBranch(Action<JsonElement> observer) =>
		connection.OpenEndpoint(Generation, observer, connection.RejectClosedRequest, static _ => { });
	internal bool ReportHealthy() => connection.ReportHealthy(Generation);
	internal void Terminate(string reason) => connection.TerminateGeneration(Generation, reason);

	// Responses answer the agent's own requests, so they still go out after retirement.
	internal Task RespondAsync(AcpClientRequest value, object result) => connection.RespondAsync(value, result);
	internal Task RespondErrorAsync(AcpClientRequest value, int code, string message, object? data) =>
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
