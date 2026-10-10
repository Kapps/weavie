using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

// The process generation stays private to the endpoint and the connection, so a conversation cannot compare one.
internal sealed class AcpSessionEndpoint(
	AcpJsonRpcConnection connection, long generation,
	Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) {
	private readonly Lock _gate = new();
	private Task<bool> _opening = Task.FromResult(true);
	private volatile bool _retired;
	private volatile bool _opened;
	internal string? SessionId { get; private set; }
	internal bool Retired => _retired;
	// Until its conversation starts opening, the connection routes the generation's unscoped traffic as before any endpoint.
	internal bool Opened => _opened;
	internal void Open() => _opened = true;
	internal void Retire() {
		lock (_gate) _retired = true;
	}
	internal void Bind(string sessionId) => connection.BindEndpoint(this, generation, sessionId);

	/// <summary>Opens a subagent's endpoint on this process, bound inside its announcement so none of its traffic precedes it.</summary>
	internal AcpSessionEndpoint OpenChild(
		string sessionId, Action<JsonElement> notification, Action<AcpClientRequest> request, Action<Exception> fault) =>
		connection.OpenChildEndpoint(generation, sessionId, notification, request, fault);

	/// <summary>Gives a subagent nobody renders a retired owner, so its traffic drops; an owned id keeps its owner.</summary>
	internal void Sink(string sessionId) => connection.SinkEndpoint(generation, sessionId);

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
	/// <summary>Cancels the session's running turn; a retired endpoint's process already ended it.</summary>
	internal Task CancelAsync() {
		lock (_gate) return _retired ? Task.CompletedTask : NotifyAsync("session/cancel", []);
	}
	internal Task<JsonElement> AuthenticateAsync(string methodId, CancellationToken ct) =>
		connection.RequestForEndpointAsync("authenticate", Parameters(new JsonObject { ["methodId"] = methodId }), this, generation, null, ct);
	internal Task<JsonElement> CreateAsync(JsonObject parameters) =>
		Opening(() => connection.CreateForEndpointAsync("session/new", Parameters(parameters), this, generation));
	internal Task<JsonElement> ForkFromAsync(AcpSessionEndpoint parent, JsonObject parameters) =>
		Opening(() => connection.CreateForEndpointAsync("session/fork", parent.Address(parameters), this, generation));
	/// <summary>Loads or resumes the bound provider session.</summary>
	internal Task<JsonElement> RestoreAsync(string method, JsonObject parameters) =>
		Opening(() => RequestAsync(method, parameters, CancellationToken.None));

	/// <summary>Retires this endpoint and closes its provider session once any opening request settles.</summary>
	internal Task CloseAsync() {
		Task<bool> opening;
		lock (_gate) {
			_retired = true;
			opening = _opening;
		}
		return connection.CloseSessionAsync(this, generation, opening);
	}

	// Registering an opening is atomic with retirement, so a close never misses a session still being opened.
	private Task<JsonElement> Opening(Func<Task<JsonElement>> start) {
		var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_retired, this);
			_opening = settled.Task;
		}
		return Settle(start, settled);
	}

	private static async Task<JsonElement> Settle(Func<Task<JsonElement>> start, TaskCompletionSource<bool> settled) {
		bool opened = false;
		try {
			var result = await start().ConfigureAwait(false);
			opened = true;
			return result;
		} finally {
			settled.SetResult(opened);
		}
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

	internal bool ReportHealthy() => connection.ReportHealthy(generation);
	internal void Terminate(string reason) => connection.TerminateGeneration(generation, reason);

	// Responses answer the agent's own requests, so they still go out after retirement.
	internal Task RespondAsync(AcpClientRequest value, JsonNode? result) => connection.RespondAsync(value, result);
	internal Task RespondErrorAsync(AcpClientRequest value, int code, string message, JsonNode? data) =>
		connection.RespondErrorAsync(value, code, message, data);
	internal void Reject(AcpClientRequest value) => connection.RejectClosedRequest(value);

	internal void Notify(JsonElement value) {
		if (!_retired) notification(value);
		else Drop(value);
	}

	/// <summary>Discards a dead conversation's notification; a subagent it announces is sunk with it.</summary>
	internal void Drop(JsonElement value) {
		if (AcpJson.SpawnedSubagent(value) is { } child) Sink(child);
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
