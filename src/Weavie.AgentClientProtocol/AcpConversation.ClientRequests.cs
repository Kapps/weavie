using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private static readonly object DeferredClientResponse = new();

	internal void RegisterClientRequest(AcpClientRequest request) {
		AcpClientRequestState state;
		lock (_turnTransitionGate) {
			if (!Live) {
				_endpoint.Value.Reject(request);
				return;
			}
			state = new AcpClientRequestState(request, _lifetime.Token);
			if (!_clientRequests.TryAdd(request.Id, state)) {
				state.Dispose();
				FailRuntimeSerialized(
					new AcpProtocolException($"ACP client request id '{request.Id}' is already active."));
				return;
			}
			if (request.Method == "session/request_permission") {
				HandlePermissionRequest(state);
				return;
			}
		}
		Run(() => HandleClientRequestAsync(state));
	}

	private async Task HandleClientRequestAsync(AcpClientRequestState state) {
		try {
			var request = state.Request;
			state.Token.ThrowIfCancellationRequested();
			ValidateRequestSession(request);
			if (request.Method is not (
				"fs/read_text_file" or "fs/write_text_file"
				or "terminal/create" or "terminal/output" or "terminal/wait_for_exit"
				or "terminal/kill" or "terminal/release" or "elicitation/create")) {
				FailClientRequest(state, -32601, $"Unsupported ACP client method '{request.Method}'.", null);
				return;
			}
			object response = request.Method switch {
				"fs/read_text_file" => ReadTextFile(request),
				"fs/write_text_file" => WriteTextFile(request),
				"terminal/create" => await CreateTerminalAsync(request, state.Token).ConfigureAwait(false),
				"terminal/output" => TerminalOutput(request),
				"terminal/wait_for_exit" => await WaitForTerminalAsync(request, state.Token).ConfigureAwait(false),
				"terminal/kill" => KillTerminal(request),
				"terminal/release" => await ReleaseTerminalAsync(request, state.Token).ConfigureAwait(false),
				"elicitation/create" => RequestInput(request, state),
				_ => throw new UnreachableException(),
			};
			CompleteClientResponse(state, response);
		} catch (Exception ex) {
			HandleClientRequestFailure(state, ex);
		}
	}

	private void CompleteClientResponse(AcpClientRequestState state, object response) {
		if (ReferenceEquals(response, DeferredClientResponse)) return;
		state.Token.ThrowIfCancellationRequested();
		CompleteClientRequest(state, response);
	}

	private void HandleClientRequestFailure(AcpClientRequestState state, Exception error) {
		if (error is OperationCanceledException && state.Token.IsCancellationRequested) {
			CancelClientRequest(state);
		} else if (FailClientRequest(state, -32002, error.Message, null)) EmitFailure(error);
	}

	private void ValidateRequestSession(AcpClientRequest request) {
		bool hasSession = request.Parameters.TryGetProperty("sessionId", out var session);
		bool hasRequest = request.Parameters.TryGetProperty("requestId", out var requestId);
		if (!hasSession && (request.Method != "elicitation/create" || !hasRequest)) {
			throw new AcpProtocolException($"ACP request {request.Id} is missing its session or request scope.");
		}
		if (hasRequest && requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) {
			throw new AcpProtocolException($"ACP request {request.Id} has an invalid requestId scope.");
		}
		if (!hasSession) {
			return;
		}
		if (session.ValueKind != JsonValueKind.String) {
			throw new AcpProtocolException($"ACP request {request.Id} has no active session.");
		}
		if (!string.Equals(session.GetString(), _endpoint.Value.SessionId, StringComparison.Ordinal)) {
			throw new AcpProtocolException($"ACP request {request.Id} targets another session.");
		}
	}

	private object ReadTextFile(AcpClientRequest request) {
		string path = RequestedPath(request.Parameters);
		string content = _context.FileSystem.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
		int line = ReadOptionalNonNegativeInt(request.Parameters, "line") ?? 1;
		int? limit = ReadOptionalNonNegativeInt(request.Parameters, "limit");
		if (line == 0) line = 1;
		string[] lines = content.Split('\n');
		int start = Math.Min(line - 1, lines.Length);
		int count = Math.Min(limit ?? lines.Length, lines.Length - start);
		return new { content = string.Join('\n', lines, start, count) };
	}

	private object WriteTextFile(AcpClientRequest request) {
		string path = RequestedPath(request.Parameters);
		string content = RequiredText(request.Parameters, "content", "fs/write_text_file request");
		var mutation = new AgentMutation.File(path, null, ProvidesEditLocation: true);
		Observe(new AgentToolStarting(mutation));
		try {
			_context.FileSystem.WriteAllText(path, content);
		} finally {
			Observe(new AgentToolCompleted(mutation));
		}
		return new { };
	}

	private static string RequestedPath(JsonElement parameters) {
		string path = RequiredString(parameters, "path", "filesystem request");
		if (!Path.IsPathFullyQualified(path)) {
			throw new AcpProtocolException($"An ACP filesystem path must be absolute: {path}");
		}
		return Path.GetFullPath(path);
	}

	private async Task<object> CreateTerminalAsync(AcpClientRequest request, CancellationToken ct) {
		string terminalId = await _terminals.CreateAsync(request.Parameters, ct).ConfigureAwait(false);
		return new { terminalId };
	}

	private object TerminalOutput(AcpClientRequest request) {
		var output = _terminals.Output(RequiredString(request.Parameters, "terminalId", "terminal/output request"));
		return new {
			output = output.Output,
			truncated = output.Truncated,
			exitStatus = ExitStatus(output.ExitStatus),
		};
	}

	private async Task<object> WaitForTerminalAsync(AcpClientRequest request, CancellationToken ct) {
		var status = await _terminals.WaitAsync(
			RequiredString(request.Parameters, "terminalId", "terminal/wait_for_exit request"),
			ct).ConfigureAwait(false);
		return ExitStatus(status)!;
	}

	private object KillTerminal(AcpClientRequest request) {
		_terminals.Kill(RequiredString(request.Parameters, "terminalId", "terminal/kill request"));
		return new { };
	}

	private async Task<object> ReleaseTerminalAsync(AcpClientRequest request, CancellationToken ct) {
		await _terminals.ReleaseAsync(
			RequiredString(request.Parameters, "terminalId", "terminal/release request"),
			ct).ConfigureAwait(false);
		return new { };
	}

	private void CompleteClientRequest(AcpClientRequestState state, object result) {
		if (!state.TryComplete()) return;
		RespondToCompletedClientRequest(state, result, errorCode: null, errorMessage: null, errorData: null);
	}

	private bool FailClientRequest(
		AcpClientRequestState state,
		int code,
		string message,
		object? data) {
		if (!state.TryComplete()) return false;
		RespondToCompletedClientRequest(state, result: null, code, message, data);
		return true;
	}

	private void RespondToCompletedClientRequest(
		AcpClientRequestState state,
		object? result,
		int? errorCode,
		string? errorMessage,
		object? errorData) {
		_clientRequests.TryRemove(state.Request.Id, out _);
		_pendingRequests.TryRemove(state.Request.Id, out _);
		if (OptionalString(state.Request.Parameters, "mode") == "url"
			&& OptionalString(state.Request.Parameters, "elicitationId") is { } elicitationId) {
			_urlElicitations.TryRemove(
				new KeyValuePair<string, string>(elicitationId, state.Request.Id));
		}
		RunRuntime(async () => {
			try {
				if (errorCode is { } code) {
					await _endpoint.Value.RespondErrorAsync(
						state.Request,
						code,
						errorMessage!,
						errorData).ConfigureAwait(false);
				} else {
					await _endpoint.Value.RespondAsync(state.Request, result!).ConfigureAwait(false);
				}
			} finally {
				state.Dispose();
			}
		});
	}

	private void CancelClientRequest(AcpClientRequestState state) {
		if (!state.TryCancel()) return;
		CancelCompletedClientRequest(state);
	}

	private void CancelCompletedClientRequest(AcpClientRequestState state) {
		CompletePermissionTool(state.Request);
		_pendingRequests.TryRemove(state.Request.Id, out var pending);
		RespondToCompletedClientRequest(state, null, -32800, "Request cancelled.", null);
		if (pending is not null) {
			ResolveInteraction(
				state.Request.Id,
				pending.Kind == "permission" ? "approval-resolved" : "input-resolved",
				"cancelled",
				pending.Kind == "permission",
				pending.ThreadId,
				pending.TurnId,
				answers: null);
		}
	}

	private void AbandonClientRequests() {
		foreach (var state in _clientRequests.Values) {
			if (!state.TryCancel()) continue;
			_clientRequests.TryRemove(state.Request.Id, out _);
			_pendingRequests.TryRemove(state.Request.Id, out var pending);
			state.Dispose();
			if (pending is not null) {
				ResolveInteraction(
					state.Request.Id,
					pending.Kind == "permission" ? "approval-resolved" : "input-resolved",
					"cancelled",
					pending.Kind == "permission",
					pending.ThreadId,
					pending.TurnId,
					answers: null);
			}
		}
		_urlElicitations.Clear();
	}

	private static object? ExitStatus(AcpTerminalExit? status) => status is null
		? null
		: new { exitCode = status.ExitCode, signal = status.Signal };
}
