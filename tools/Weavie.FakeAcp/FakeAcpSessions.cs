using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed class FakeAcpSessions : IAcpAgent {
	private readonly FakeAcpAgent _process = new();
	private readonly ConcurrentDictionary<string, FakeAcpAgent> _sessions = new(StringComparer.Ordinal);
	private AcpAgentConnection? _connection;

	public Task TerminalFailure => _process.TerminalFailure;

	public void Attach(AcpAgentConnection connection) {
		_connection = connection;
		_process.Attach(connection);
	}

	public async Task<JsonNode> HandleRequestAsync(
		JsonElement requestId, string method, JsonElement parameters, CancellationToken ct) {
		if (method is "initialize" or "authenticate") {
			var result = await _process.HandleRequestAsync(requestId, method, parameters, ct).ConfigureAwait(false);
			foreach (var session in _sessions.Values) session.CopyConnectionState(_process);
			return result;
		}
		if (method is "session/new" or "session/load" or "session/resume") {
			string? id = method == "session/new" ? null : AcpJson.RequiredString(parameters, "sessionId", method);
			var session = id is not null && _sessions.TryGetValue(id, out var existing) ? existing : CreateSession();
			var result = await session.HandleRequestAsync(requestId, method, parameters, ct).ConfigureAwait(false);
			id = result["sessionId"]!.GetValue<string>();
			_sessions[id] = session;
			return result;
		}
		return await FindSession(parameters).HandleRequestAsync(requestId, method, parameters, ct).ConfigureAwait(false);
	}

	public Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken ct) =>
		FindSession(parameters).HandleNotificationAsync(method, parameters, ct);

	public async ValueTask DisposeAsync() {
		foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
		await _process.DisposeAsync().ConfigureAwait(false);
	}

	private FakeAcpAgent CreateSession() {
		var session = new FakeAcpAgent();
		session.Attach(_connection ?? throw new InvalidOperationException("Fake ACP is not attached."));
		session.CopyConnectionState(_process);
		return session;
	}

	private FakeAcpAgent FindSession(JsonElement parameters) {
		string id = AcpJson.RequiredString(parameters, "sessionId", "session request");
		return _sessions.TryGetValue(id, out var session)
			? session : throw AcpAdapterException.InvalidParams($"Unknown fake session '{id}'.");
	}
}
