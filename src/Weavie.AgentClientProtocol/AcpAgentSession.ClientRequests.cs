using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private static readonly object DeferredClientResponse = new();

	private void RegisterClientRequest(AcpClientRequest request) {
		AcpClientRequestState state;
		lock (_turnTransitionGate) {
			if (!OwnsGeneration(request.Generation)) return;
			state = new AcpClientRequestState(request);
			if (!_clientRequests.TryAdd(request.Id, state)) {
				state.Dispose();
				FailRuntimeSerialized(
					new AcpProtocolException($"ACP client request id '{request.Id}' is already active."));
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
				"fs/read_text_file" or "fs/write_text_file" or "session/request_permission"
				or "terminal/create" or "terminal/output" or "terminal/wait_for_exit"
				or "terminal/kill" or "terminal/release" or "elicitation/create")) {
				FailClientRequest(state, -32601, $"Unsupported ACP client method '{request.Method}'.", null);
				return;
			}
			object response = request.Method switch {
				"fs/read_text_file" => ReadTextFile(request),
				"fs/write_text_file" => WriteTextFile(request),
				"session/request_permission" => RequestPermission(request, state),
				"terminal/create" => await CreateTerminalAsync(request, state.Token).ConfigureAwait(false),
				"terminal/output" => TerminalOutput(request),
				"terminal/wait_for_exit" => await WaitForTerminalAsync(request, state.Token).ConfigureAwait(false),
				"terminal/kill" => KillTerminal(request),
				"terminal/release" => await ReleaseTerminalAsync(request, state.Token).ConfigureAwait(false),
				"elicitation/create" => RequestInput(request, state),
				_ => throw new UnreachableException(),
			};
			if (!ReferenceEquals(response, DeferredClientResponse)) {
				state.Token.ThrowIfCancellationRequested();
				CompleteClientRequest(state, response);
			}
		} catch (OperationCanceledException) when (state.Token.IsCancellationRequested) {
			CancelClientRequest(state);
		} catch (Exception ex) {
			if (FailClientRequest(state, -32002, ex.Message, null)) EmitFailure(ex);
		}
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
		if (session.ValueKind != JsonValueKind.String || SessionId() is not { } current) {
			throw new AcpProtocolException($"ACP request {request.Id} has no active session.");
		}
		if (!string.Equals(session.GetString(), current, StringComparison.Ordinal)) {
			throw new AcpProtocolException($"ACP request {request.Id} targets another session.");
		}
	}

	private object ReadTextFile(AcpClientRequest request) {
		string path = AllowedPath(request.Parameters, allowMissingLeaf: false);
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
		string path = AllowedPath(request.Parameters, allowMissingLeaf: true);
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

	private string AllowedPath(JsonElement parameters, bool allowMissingLeaf) {
		string path = RequiredString(parameters, "path", "filesystem request");
		try {
			return _fileScope.ResolvePhysicalPath(path, allowMissingLeaf);
		} catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) {
			throw new UnauthorizedAccessException($"ACP filesystem request is outside the workspace: {path}");
		}
	}

	private object RequestPermission(AcpClientRequest request, AcpClientRequestState state) {
		if (!request.Parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("An ACP permission request is missing options.");
		}
		if (_context.Settings.RequireBool(AgentSettings.AllowAllPermissions)) {
			string? optionId = options.EnumerateArray()
				.Where(option => OptionalString(option, "kind") is "allow_always" or "allow_once")
				.OrderBy(option => OptionalString(option, "kind") == "allow_always" ? 0 : 1)
				.Select(option => OptionalString(option, "optionId"))
				.FirstOrDefault(id => id is not null);
			string selected = optionId ?? throw new AcpProtocolException(
				"Permission bypass is enabled, but ACP advertised no allow option.");
			return new { outcome = new { outcome = "selected", optionId = selected } };
		}

		if (!request.Parameters.TryGetProperty("toolCall", out var tool) || tool.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("An ACP permission request is missing toolCall.");
		}
		var actions = options.EnumerateArray().Select(option => new AgentActionOption {
			Id = RequiredString(option, "optionId", "permission option"),
			Label = RequiredString(option, "name", "permission option"),
			Kind = RequiredString(option, "kind", "permission option"),
		}).ToArray();
		string? threadId = SessionId();
		string turnId = TurnId();
		if (!_pendingRequests.TryAdd(
			request.Id,
			new AcpPendingRequest(request, "permission", options.Clone(), threadId, turnId))) {
			throw new AcpProtocolException($"ACP request id '{request.Id}' is already pending.");
		}
		if (!state.PublishDeferred(() => {
			Observe(new AgentPermissionRequested());
			Observe(new AgentPermissionResolved(RequiresUserInput: true));
			Emit(new AgentPaneMessage {
				Type = "approval-requested",
				ProviderId = _definition.Id,
				ThreadId = threadId,
				TurnId = turnId,
				ItemId = $"request:{request.Id}",
				RequestId = request.Id,
				ItemType = OptionalString(tool, "kind") ?? "tool",
				Category = OptionalString(tool, "kind"),
				Summary = OptionalString(tool, "title") ?? "Permission requested",
				Text = ToolRequestText(tool),
				Actions = actions,
				Status = "pending",
			});
		})) {
			_pendingRequests.TryRemove(request.Id, out _);
			state.Token.ThrowIfCancellationRequested();
		}
		return DeferredClientResponse;
	}

	private static string? ToolRequestText(JsonElement tool) {
		if (tool.TryGetProperty("rawInput", out var rawInput)) {
			return rawInput.ValueKind == JsonValueKind.String ? rawInput.GetString() : rawInput.GetRawText();
		}
		return null;
	}

	private async Task<object> CreateTerminalAsync(AcpClientRequest request, CancellationToken ct) {
		string terminalId = await _terminals.CreateAsync(
			request.Parameters,
			request.Generation,
			ct).ConfigureAwait(false);
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
		RunRuntime(state.Request.Generation, async () => {
			try {
				if (errorCode is { } code) {
					await _connection.RespondErrorAsync(
						state.Request,
						code,
						errorMessage!,
						errorData).ConfigureAwait(false);
				} else {
					await _connection.RespondAsync(state.Request, result!).ConfigureAwait(false);
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
		_pendingRequests.TryRemove(state.Request.Id, out var pending);
		RespondToCompletedClientRequest(state, null, -32800, "Request cancelled.", null);
		if (pending is not null) {
			ResolveInteraction(
				state.Request.Id,
				pending.Kind == "permission" ? "approval-resolved" : "input-resolved",
				"cancelled",
				pending.Kind == "permission",
				pending.ThreadId,
				pending.TurnId);
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
					pending.TurnId);
			}
		}
		_urlElicitations.Clear();
	}

	private static int? ReadOptionalNonNegativeInt(JsonElement value, string property) {
		if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null) {
			return null;
		}
		if (!result.TryGetInt32(out int number) || number < 0) {
			throw new AcpProtocolException($"'{property}' must be a non-negative integer.");
		}
		return number;
	}

	private static double? ReadOptionalDouble(JsonElement value, string property) {
		if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null) {
			return null;
		}
		if (!result.TryGetDouble(out double number) || !double.IsFinite(number)) {
			throw new AcpProtocolException($"'{property}' must be a finite number.");
		}
		return number;
	}

	private static object? ExitStatus(AcpTerminalExit? status) => status is null
		? null
		: new { exitCode = status.ExitCode, signal = status.Signal };
}
