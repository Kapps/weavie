using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.AgentClientProtocol;

/// <summary>
/// The JSON-RPC transport for one transient ACP process: launched once, never restarted, and killed as a tree on
/// disposal. Owners decide how agent requests and notifications are answered.
/// </summary>
internal sealed class AcpTransientConnection : IAsyncDisposable {
	private const int StderrTailLines = 12;
	private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
	private readonly Queue<string> _stderr = new();
	private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly OwnedProcess _process;
	private readonly Task _stderrDrained;
	private long _nextId;
	private volatile bool _disposed;

	private AcpTransientConnection(AcpAgentDefinition definition, OwnedProcess process) {
		Definition = definition;
		_process = process;
		_stderrDrained = DrainStderrAsync();
	}

	/// <summary>The agent this process runs.</summary>
	public AcpAgentDefinition Definition { get; }

	/// <summary>The user-facing failure for an agent that demands a sign-in a transient client can't perform.</summary>
	public string AuthenticationRequired => $"{Definition.Name} requires authentication. Open a session with it to sign in.";

	/// <summary>Launches the agent in <paramref name="workspace"/>; call <see cref="Listen"/> before any request.</summary>
	public static AcpTransientConnection Start(AcpAgentDefinition definition, string workspace) {
		string directory = Path.GetFullPath(workspace);
		var invocation = AcpProcessInvocation.ResolveRedirectedProcess(definition, directory, []);
		var info = new ProcessStartInfo(invocation.Command) {
			WorkingDirectory = directory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = Utf8NoBom,
			StandardOutputEncoding = Utf8NoBom,
			StandardErrorEncoding = Utf8NoBom,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (string argument in invocation.Arguments) info.ArgumentList.Add(argument);
		foreach (var entry in definition.Environment) info.Environment[entry.Key] = entry.Value;

		return new AcpTransientConnection(definition, OwnedProcess.Start(info));
	}

	/// <summary>Stops the agent and describes <paramref name="failure"/> with the agent's own last stderr lines.</summary>
	public async Task<InvalidOperationException> FailureAsync(string summary, Exception failure) {
		await DisposeAsync().ConfigureAwait(false);
		// The agent's own explanation (e.g. an npm error) is on stderr, which can still be flushing after stdout closes.
		await _stderrDrained.ConfigureAwait(false);
		string output;
		lock (_stderr) output = string.Join('\n', _stderr).Trim();
		return new InvalidOperationException(
			$"{summary}: {failure.Message}" + (output.Length > 0 ? $"\n{output}" : string.Empty), failure);
	}

	/// <summary>True when launching failed because the agent's command could not be started.</summary>
	public static bool IsStartFailure(Exception ex) => ex is Win32Exception or FileNotFoundException or IOException
		or InvalidOperationException or UnauthorizedAccessException;

	/// <summary>Starts reading the agent's output; each agent request and notification goes to its callback.</summary>
	public void Listen(Action<long, string, JsonElement> onRequest, Action<string, JsonElement> onNotification) =>
		_ = ReadStdoutAsync(onRequest, onNotification);

	// Read stderr so a chatty agent cannot fill its pipe buffer and stall; its tail explains a failed start.
	private async Task DrainStderrAsync() {
		try {
			while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) {
				lock (_stderr) {
					_stderr.Enqueue(line);
					if (_stderr.Count > StderrTailLines) _stderr.Dequeue();
				}
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			// The process ended first.
		}
	}

	/// <summary>Sends one request and waits for its result, or throws the agent's error.</summary>
	public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		long id = Interlocked.Increment(ref _nextId);
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = completion;
		try {
			await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }).ConfigureAwait(false);
		} catch {
			_pending.TryRemove(id, out _);
			throw;
		}

		using var registration = ct.Register(() => {
			if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled(ct);
		});
		return await completion.Task.ConfigureAwait(false);
	}

	/// <summary>Sends a notification, ignoring an agent that is already gone.</summary>
	public Task NotifyAsync(string method, object parameters) =>
		WriteIgnoringExitAsync(new { jsonrpc = "2.0", method, @params = parameters });

	/// <summary>Answers an agent request with <paramref name="result"/>.</summary>
	public Task RespondAsync(long id, object result) => WriteIgnoringExitAsync(new { jsonrpc = "2.0", id, result });

	/// <summary>Refuses an agent request this client does not serve.</summary>
	public Task RefuseAsync(long id, string method) => WriteIgnoringExitAsync(new {
		jsonrpc = "2.0",
		id,
		error = new { code = -32601, message = $"Weavie does not serve '{method}' here." },
	});

	private async Task WriteIgnoringExitAsync(object payload) {
		try {
			await WriteAsync(payload).ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
			// The agent is already gone; the pending request fails on its own.
		}
	}

	private async Task WriteAsync(object payload) {
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
		await _process.StandardInput.FlushAsync().ConfigureAwait(false);
	}

	private async Task ReadStdoutAsync(Action<long, string, JsonElement> onRequest, Action<string, JsonElement> onNotification) {
		try {
			while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
				if (line.Length == 0) continue;
				try {
					Handle(line, onRequest, onNotification);
				} catch (JsonException) {
					FailPending(new AcpProtocolException("The ACP agent wrote output that isn't an ACP message."));
					return;
				}
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			// Falls through to the shared teardown below.
		}

		FailPending(new IOException("The ACP agent closed its output before answering."));
	}

	private void Handle(string line, Action<long, string, JsonElement> onRequest, Action<string, JsonElement> onNotification) {
		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;
		bool hasId = root.TryGetProperty("id", out var id);
		string? method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
			? m.GetString()
			: null;
		var parameters = root.TryGetProperty("params", out var p) ? p : default;

		if (hasId && method is null) {
			if (!_pending.TryRemove(ReadId(id), out var pending)) return;
			if (root.TryGetProperty("error", out var error)) {
				pending.TrySetException(ReadError(error));
			} else if (root.TryGetProperty("result", out var result)) {
				pending.TrySetResult(result.Clone());
			} else {
				pending.TrySetException(new AcpProtocolException("An ACP response carried no result or error."));
			}
		} else if (hasId && method is not null) {
			onRequest(ReadId(id), method, parameters);
		} else if (method is not null) {
			onNotification(method, parameters);
		}
	}

	private static long ReadId(JsonElement id) =>
		id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value) ? value : -1;

	private static Exception ReadError(JsonElement error) {
		int code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
			&& c.TryGetInt32(out int parsed)
				? parsed
				: 0;
		string message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
			? m.GetString() ?? "unknown"
			: "unknown";
		return code == -32000
			? new AcpAuthenticationRequiredException(message)
			: new AcpProtocolException($"The ACP agent rejected a request: {message}");
	}

	private void FailPending(Exception fault) {
		foreach (long key in _pending.Keys) {
			if (_pending.TryRemove(key, out var pending)) pending.TrySetException(fault);
		}
	}

	public async ValueTask DisposeAsync() {
		if (_disposed) return;
		_disposed = true;
		FailPending(new ObjectDisposedException(nameof(AcpTransientConnection)));
		try {
			if (!_process.HasExited) _process.Kill(entireProcessTree: true);
		} catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception) {
			// The process already exited; nothing to terminate.
		}

		try {
			await _process.WaitForExitAsync().ConfigureAwait(false);
		} catch (InvalidOperationException) {
			// The handle is already gone.
		} finally {
			_process.Dispose();
		}
	}
}

/// <summary>The ACP agent demanded authentication that a transient client cannot perform.</summary>
internal sealed class AcpAuthenticationRequiredException : Exception {
	public AcpAuthenticationRequiredException(string message) : base(message) { }
}
