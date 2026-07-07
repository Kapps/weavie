using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Supervises <c>codex app-server --stdio</c> and exchanges JSON-RPC messages over JSONL.</summary>
public sealed class CodexAppServerClient : IAsyncDisposable {
	private readonly string _command;
	private readonly string _workingDirectory;
	private readonly IReadOnlyList<string> _configArguments;
	private readonly Action<string> _log;
	private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly Lock _gate = new();
	private readonly ProcessSupervisor _supervisor;
	private Process? _process;

	/// <summary>Creates a client for a Codex executable rooted at <paramref name="workingDirectory"/>.</summary>
	public CodexAppServerClient(string command, string workingDirectory, IReadOnlyList<string> configArguments, Action<string> log) {
		ArgumentException.ThrowIfNullOrEmpty(command);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentNullException.ThrowIfNull(configArguments);
		ArgumentNullException.ThrowIfNull(log);
		_command = command;
		_workingDirectory = workingDirectory;
		_configArguments = configArguments;
		_log = log;
		_supervisor = new ProcessSupervisor(
			"codex:app-server",
			StartProcess,
			StopProcess,
			new SupervisionOptions { Policy = RestartPolicy.Always },
			entry => _log($"[codex-app-server] {entry.Level}: {entry.Message}"),
			clock: null);
		_supervisor.StateChanged += change => ProcessStateChanged?.Invoke(change);
	}

	/// <summary>Raised for server notifications that do not expect a response.</summary>
	public event Action<JsonElement>? NotificationReceived;

	/// <summary>Raised for server-initiated requests that must be answered with <see cref="Respond"/>.</summary>
	public event Action<CodexServerRequest>? RequestReceived;

	/// <summary>Raised each time the supervised app-server process starts or restarts.</summary>
	public event Action<int>? ProcessStarted;

	/// <summary>Raised when the supervised app-server lifecycle changes state.</summary>
	public event Action<SupervisorStateChanged>? ProcessStateChanged;

	/// <summary>Starts the supervised app-server process.</summary>
	public void Start() => _supervisor.Start();

	/// <summary>Sends a request line and completes with the response result.</summary>
	public async Task<JsonElement> RequestAsync(long id, string line, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(line);
		var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_pending.TryAdd(id, pending)) {
			throw new InvalidOperationException($"Codex request id {id} is already pending.");
		}

		using var registration = ct.Register(() => {
			if (_pending.TryRemove(id, out var task)) {
				task.TrySetCanceled(ct);
			}
		});
		try {
			WriteLine(line);
		} catch {
			_pending.TryRemove(id, out _);
			throw;
		}

		return await pending.Task.ConfigureAwait(false);
	}

	/// <summary>Sends a notification line.</summary>
	public void Notify(string line) {
		ArgumentException.ThrowIfNullOrEmpty(line);
		WriteLine(line);
	}

	/// <summary>Responds to a server-initiated request.</summary>
	public void Respond(object id, object result) {
		ArgumentNullException.ThrowIfNull(id);
		ArgumentNullException.ThrowIfNull(result);
		WriteLine(JsonSerializer.Serialize(new { id, result }));
	}

	/// <summary>Responds to a server-initiated request with a JSON-RPC error.</summary>
	public void RespondError(object id, int code, string message) {
		ArgumentNullException.ThrowIfNull(id);
		ArgumentException.ThrowIfNullOrEmpty(message);
		WriteLine(JsonSerializer.Serialize(new { id, error = new { code, message } }));
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		_supervisor.Dispose();
		FailPending(new ObjectDisposedException(nameof(CodexAppServerClient)));
		return ValueTask.CompletedTask;
	}

	private void StartProcess(int attempt) {
		var process = new Process {
			StartInfo = new ProcessStartInfo(_command) {
				WorkingDirectory = _workingDirectory,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			},
			EnableRaisingEvents = true,
		};
		process.StartInfo.ArgumentList.Add("app-server");
		foreach (string argument in _configArguments) {
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.StartInfo.ArgumentList.Add("--stdio");
		process.Exited += (_, _) => {
			int exitCode = ReadExitCode(process);
			_log($"[codex-app-server] exited {exitCode}");
			FailPending(new IOException($"Codex app-server exited with code {exitCode}."));
			_supervisor.NotifyExited(exitCode);
		};
		if (!process.Start()) {
			throw new InvalidOperationException("Codex app-server did not start.");
		}

		lock (_gate) {
			_process = process;
		}

		ProcessStarted?.Invoke(attempt);
		_ = ReadStdoutAsync(process);
		_ = ReadStderrAsync(process);
	}

	private void StopProcess() {
		Process? process;
		lock (_gate) {
			process = _process;
			_process = null;
		}

		if (process is null) {
			return;
		}

		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) {
			_log($"[codex-app-server] stop failed: {ex.Message}");
		} finally {
			process.Dispose();
		}
	}

	private async Task ReadStdoutAsync(Process process) {
		try {
			while (!process.HasExited && await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
				HandleLine(line);
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or JsonException) {
			_log($"[codex-app-server] stdout closed: {ex.Message}");
		}
	}

	private async Task ReadStderrAsync(Process process) {
		try {
			while (!process.HasExited && await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) {
				_log($"[codex-app-server] {line}");
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			_log($"[codex-app-server] stderr closed: {ex.Message}");
		}
	}

	private void HandleLine(string line) {
		if (string.IsNullOrWhiteSpace(line)) {
			return;
		}

		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;
		if (root.TryGetProperty("id", out var idElement)) {
			if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String) {
				RaiseRequest(new CodexServerRequest(
					ReadRequestId(idElement),
					ReadResponseId(idElement),
					methodElement.GetString() ?? string.Empty,
					root.Clone()));
				return;
			}

			if (idElement.ValueKind == JsonValueKind.Number
				&& idElement.TryGetInt64(out long id)
				&& _pending.TryRemove(id, out var pending)) {
				if (root.TryGetProperty("error", out var error)) {
					pending.TrySetException(new InvalidOperationException(error.ToString()));
				} else {
					pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : root.Clone());
				}
			}

			return;
		}

		RaiseNotification(root.Clone());
	}

	private void RaiseNotification(JsonElement root) {
		var handlers = NotificationReceived;
		if (handlers is null) {
			return;
		}

		foreach (Action<JsonElement> handler in handlers.GetInvocationList()) {
			try {
				handler(root);
			} catch (Exception ex) {
				_log($"[codex-app-server] notification handler failed: {ex.Message}");
			}
		}
	}

	private void RaiseRequest(CodexServerRequest request) {
		var handlers = RequestReceived;
		if (handlers is null) {
			return;
		}

		foreach (Action<CodexServerRequest> handler in handlers.GetInvocationList()) {
			try {
				handler(request);
			} catch (Exception ex) {
				_log($"[codex-app-server] request handler failed: {ex.Message}");
			}
		}
	}

	private void WriteLine(string line) {
		Process? process;
		lock (_gate) {
			process = _process;
		}

		if (process is null || process.HasExited) {
			throw new InvalidOperationException("Codex app-server is not running.");
		}

		process.StandardInput.WriteLine(line);
		process.StandardInput.Flush();
	}

	private void FailPending(Exception exception) {
		foreach (long id in _pending.Keys) {
			if (_pending.TryRemove(id, out var pending)) {
				pending.TrySetException(exception);
			}
		}
	}

	private static int ReadExitCode(Process process) {
		try {
			return process.ExitCode;
		} catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) {
			return -1;
		}
	}

	private static string ReadRequestId(JsonElement id) =>
		id.ValueKind == JsonValueKind.String
			? id.GetString() ?? string.Empty
			: id.GetRawText();

	private static object ReadResponseId(JsonElement id) {
		if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long number)) {
			return number;
		}

		if (id.ValueKind == JsonValueKind.String) {
			return id.GetString() ?? string.Empty;
		}

		throw new JsonException("Codex request id must be a string or integer.");
	}
}

/// <summary>A JSON-RPC request initiated by Codex app-server.</summary>
public sealed record CodexServerRequest(string Id, object ResponseId, string Method, JsonElement Message);
