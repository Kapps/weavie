using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>Runs one strict concurrent ACP JSON-RPC server over standard I/O.</summary>
public sealed class AcpAgentServer {
	private readonly IAcpAgent _agent;
	private readonly TextReader _input;
	private readonly TextWriter _output;
	private readonly TextWriter _error;
	private readonly Lock _writeGate = new();
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly ConcurrentDictionary<long, byte> _cancelled = new();
	private readonly ConcurrentDictionary<int, Task> _dispatches = new();
	private readonly TaskCompletionSource _dispatchFailure =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _nextDispatchId;

	/// <summary>Creates a server for <paramref name="agent"/>.</summary>
	public AcpAgentServer(IAcpAgent agent, TextReader input, TextWriter output, TextWriter error) {
		ArgumentNullException.ThrowIfNull(agent);
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(error);
		_agent = agent;
		_input = input;
		_output = output;
		_error = error;
		_agent.Attach(new AcpAgentConnection(output, _writeGate, _pending, _cancelled));
	}

	/// <summary>Runs until standard input closes or cancellation is requested.</summary>
	public async Task RunAsync(CancellationToken ct) {
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var terminalFailure = _agent.TerminalFailure;
		var input = Task.Run(() => ReadInputAsync(lifetime.Token), CancellationToken.None);
		try {
			var completed = await Task.WhenAny(input, terminalFailure, _dispatchFailure.Task).ConfigureAwait(false);
			if (ReferenceEquals(completed, terminalFailure)) {
				lifetime.Cancel();
				await terminalFailure.ConfigureAwait(false);
			} else if (ReferenceEquals(completed, _dispatchFailure.Task)) {
				lifetime.Cancel();
				await _dispatchFailure.Task.ConfigureAwait(false);
			} else {
				await input.ConfigureAwait(false);
			}
		} finally {
			lifetime.Cancel();
			foreach (var request in _requests.Values) request.Cancel();
			foreach (var completion in _pending.Values) completion.TrySetException(new EndOfStreamException("ACP client disconnected."));
			await Task.WhenAll(_dispatches.Values).ConfigureAwait(false);
			foreach (var request in _requests.Values) request.Dispose();
			await _agent.DisposeAsync().ConfigureAwait(false);
		}
	}

	private async Task ReadInputAsync(CancellationToken ct) {
		while (await _input.ReadLineAsync(ct).ConfigureAwait(false) is { } line) {
			if (line.Length == 0) continue;
			Dispatch(line, ct);
		}
	}

	private void Dispatch(string line, CancellationToken connectionToken) {
		JsonElement root;
		try {
			using var document = JsonDocument.Parse(line);
			root = document.RootElement.Clone();
			ValidateEnvelope(root);
		} catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
			throw new JsonException($"ACP client sent invalid JSON-RPC: {ex.Message}", ex);
		}

		if (root.TryGetProperty("method", out var methodValue)) {
			string method = methodValue.GetString() ?? throw new JsonException("ACP method cannot be null.");
			var parameters = root.TryGetProperty("params", out var value) ? value : EmptyParameters();
			if (root.TryGetProperty("id", out var id)) {
				Track(HandleRequestAsync(id, method, parameters, connectionToken));
			} else if (method == "$/cancel_request") {
				CancelClientRequest(parameters);
			} else {
				Track(HandleNotificationAsync(method, parameters, connectionToken));
			}
			return;
		}

		HandleResponse(root);
	}

	private async Task HandleRequestAsync(
		JsonElement id,
		string method,
		JsonElement parameters,
		CancellationToken connectionToken) {
		string key = AcpJson.IdKey(id);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
		if (!_requests.TryAdd(key, cancellation)) {
			WriteError(id, -32600, $"ACP request id '{key}' is already active.", null);
			return;
		}
		try {
			var result = await _agent.HandleRequestAsync(id, method, parameters, cancellation.Token).ConfigureAwait(false);
			WriteResult(id, result);
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
			if (DispatchError() is { } error) WriteError(id, -32603, error.Message, null);
			else WriteError(id, -32800, "Request cancelled.", null);
		} catch (AcpAdapterException ex) {
			WriteError(id, ex.Code, ex.Message, ex.DataPayload);
		} catch (Exception ex) {
			_error.WriteLine(ex);
			_error.Flush();
			WriteError(id, -32603, ex.Message, null);
		} finally {
			_requests.TryRemove(key, out _);
		}
	}

	private async Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken ct) {
		try {
			await _agent.HandleNotificationAsync(method, parameters, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			_error.WriteLine(ex);
			_error.Flush();
			throw new InvalidOperationException($"ACP notification '{method}' failed: {ex.Message}", ex);
		}
	}

	private Exception? DispatchError() => _dispatchFailure.Task.Exception?.InnerException;

	private void HandleResponse(JsonElement root) {
		var id = root.GetProperty("id");
		if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long number)) {
			throw new JsonException("ACP client responses must use the numeric id assigned by the agent.");
		}
		bool hasResult = root.TryGetProperty("result", out var result);
		bool hasError = root.TryGetProperty("error", out var error);
		if (hasResult == hasError) {
			throw new JsonException("ACP responses require exactly one of result or error.");
		}
		if (_pending.TryRemove(number, out var completion)) {
			if (hasError) completion.TrySetException(ParseResponseError(error));
			else completion.TrySetResult(result.Clone());
			return;
		}
		if (_cancelled.TryRemove(number, out _)) {
			return;
		}
		throw new JsonException($"ACP client returned unsolicited response id {number}.");
	}

	private void CancelClientRequest(JsonElement parameters) {
		if (!parameters.TryGetProperty("requestId", out var id)) {
			throw new JsonException("ACP cancellation requires requestId.");
		}
		if (_requests.TryGetValue(AcpJson.IdKey(id), out var cancellation)) {
			cancellation.Cancel();
		}
	}

	private void Track(Task dispatch) {
		int id = Interlocked.Increment(ref _nextDispatchId);
		if (!_dispatches.TryAdd(id, dispatch)) {
			throw new InvalidOperationException($"ACP dispatch id {id} is already active.");
		}
		_ = dispatch.ContinueWith(
			completed => {
				_dispatches.TryRemove(id, out _);
				if (completed.IsFaulted) {
					_dispatchFailure.TrySetException(
						completed.Exception?.InnerException ?? new InvalidOperationException("ACP dispatch failed."));
				}
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private void WriteResult(JsonElement id, JsonNode result) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = AcpJson.Clone(id),
		["result"] = result,
	});

	private void WriteError(JsonElement id, int code, string message, JsonNode? data) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = AcpJson.Clone(id),
		["error"] = new JsonObject {
			["code"] = code,
			["message"] = message,
			["data"] = data,
		},
	});

	private void Write(JsonNode message) {
		lock (_writeGate) {
			_output.WriteLine(message.ToJsonString());
			_output.Flush();
		}
	}

	private static void ValidateEnvelope(JsonElement root) {
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("jsonrpc", out var version)
			|| version.GetString() != "2.0") {
			throw new JsonException("ACP messages must be JSON-RPC 2.0 objects.");
		}
		bool hasMethod = root.TryGetProperty("method", out var method);
		bool hasId = root.TryGetProperty("id", out var id);
		if (hasMethod && (method.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(method.GetString()))) {
			throw new JsonException("ACP methods must be non-empty strings.");
		}
		if (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) {
			throw new JsonException("ACP ids must be strings or numbers.");
		}
		if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind != JsonValueKind.Object) {
			throw new JsonException("ACP params must be objects.");
		}
		bool hasResult = root.TryGetProperty("result", out _);
		bool hasError = root.TryGetProperty("error", out var error);
		if (hasMethod && (hasResult || hasError)) {
			throw new JsonException("ACP requests and notifications cannot contain result or error.");
		}
		if (!hasMethod && !hasId) {
			throw new JsonException("ACP responses require an id.");
		}
		if (!hasMethod && hasResult == hasError) {
			throw new JsonException("ACP responses require exactly one of result or error.");
		}
		if (hasError && error.ValueKind != JsonValueKind.Object) {
			throw new JsonException("ACP response errors must be objects.");
		}
	}

	private static AcpAdapterException ParseResponseError(JsonElement error) {
		if (!error.TryGetProperty("code", out var codeValue)
			|| !codeValue.TryGetInt32(out int code)
			|| !error.TryGetProperty("message", out var messageValue)
			|| messageValue.ValueKind != JsonValueKind.String) {
			throw new JsonException("ACP client response errors require an integer code and string message.");
		}
		var data = error.TryGetProperty("data", out var dataValue) && dataValue.ValueKind != JsonValueKind.Null
			? AcpJson.Clone(dataValue)
			: null;
		return new AcpAdapterException(code, messageValue.GetString() ?? string.Empty, data);
	}

	private static JsonElement EmptyParameters() {
		using var document = JsonDocument.Parse("{}");
		return document.RootElement.Clone();
	}
}
