using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.AgentClientProtocol;

/// <summary>A strict bidirectional JSON-RPC 2.0 connection to one supervised ACP agent process.</summary>
public sealed partial class AcpJsonRpcConnection : IAsyncDisposable {
	private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
	private readonly Func<AcpAgentDefinition> _currentDefinition;
	private readonly string _providerId;
	private readonly string _providerName;
	private readonly string _workingDirectory;
	private readonly Action<string> _log;
	private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
	private readonly ConcurrentDictionary<long, byte> _cancelled = new();
	private readonly Lock _processGate = new();
	private readonly Lock _deliveryGate = new();
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly ProcessSupervisor _supervisor;
	private OwnedProcess? _process;
	private long _processGeneration;
	private long _faultedGeneration;
	private long _nextRequestId;
	private bool _disposed;

	/// <summary>Creates a process connection rooted at <paramref name="workingDirectory"/>.</summary>
	public AcpJsonRpcConnection(
		AcpAgentDefinition definition,
		string workingDirectory,
		Action<string> log) : this(() => definition, workingDirectory, log) { }

	internal AcpJsonRpcConnection(
		Func<AcpAgentDefinition> definition,
		string workingDirectory,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentNullException.ThrowIfNull(log);
		_currentDefinition = definition;
		var initial = definition() ?? throw new InvalidOperationException("The ACP agent definition is unavailable.");
		_providerId = initial.Id;
		_providerName = initial.Name;
		_workingDirectory = workingDirectory;
		_log = log;
		_supervisor = new ProcessSupervisor(
			$"acp:{initial.Id}",
			StartProcess,
			StopProcess,
			new SupervisionOptions {
				Policy = RestartPolicy.Never,
				RequireExplicitHealth = true,
				HealthyAfter = TimeSpan.Zero,
			},
			entry => _log($"[acp:{initial.Id}] {entry.Level}: {entry.Message}"),
			new SystemSupervisorClock());
		_supervisor.StateChanged += change => ProcessStateChanged?.Invoke(change);
	}

	/// <summary>Raised for ACP notifications.</summary>
	public event Action<long, JsonElement>? NotificationReceived;

	/// <summary>Raised for ACP requests initiated by the agent.</summary>
	public event Action<AcpClientRequest>? RequestReceived;

	/// <summary>Raised after a fresh agent process generation has started.</summary>
	public event Action<AcpProcessGeneration>? ProcessStarted;

	/// <summary>Raised for supervised process lifecycle changes.</summary>
	public event Action<SupervisorStateChanged>? ProcessStateChanged;

	/// <summary>Raised when strict ACP framing is violated.</summary>
	public event Action<long, Exception>? ProtocolFaulted;

	/// <summary>Starts the supervised adapter.</summary>
	public void Start() => _supervisor.Start();

	/// <summary>Intentionally replaces the current agent process generation.</summary>
	public void Restart() {
		FailPending(new IOException("ACP agent restarted."));
		_supervisor.Stop();
		_supervisor.Start();
	}

	/// <summary>Marks a fully initialized process generation healthy.</summary>
	public bool ReportHealthy(long generation) => _supervisor.ReportHealthy(generation);

	internal bool IsLatestGeneration(long generation) => _supervisor.Generation == generation;

	/// <summary>Sends a request and returns its result.</summary>
	public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) =>
		RequestAsync(method, parameters, expectedGeneration: null, ct);

	internal Task<JsonElement> RequestAsync(
		string method,
		object parameters,
		long expectedGeneration,
		CancellationToken ct) =>
		RequestAsync(method, parameters, (long?)expectedGeneration, ct);

	private async Task<JsonElement> RequestAsync(
		string method,
		object parameters,
		long? expectedGeneration,
		CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(method);
		ArgumentNullException.ThrowIfNull(parameters);
		ct.ThrowIfCancellationRequested();
		long id = Interlocked.Increment(ref _nextRequestId);
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		try {
			await WriteRequestAsync(id, method, parameters, expectedGeneration, completion).ConfigureAwait(false);
		} catch {
			_pending.TryRemove(id, out _);
			throw;
		}
		using var registration = ct.Register(() => CancelRequest(id, ct));
		return await completion.Task.ConfigureAwait(false);
	}

	/// <summary>Immediately stops one exact agent process generation after an unrecoverable host failure.</summary>
	public bool TerminateGeneration(long generation, string reason) {
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
		ArgumentException.ThrowIfNullOrEmpty(reason);
		return _supervisor.ReportUnhealthy(generation, reason);
	}

	/// <summary>Sends an ACP notification.</summary>
	public Task NotifyAsync(string method, object parameters) =>
		NotifyAsync(method, parameters, expectedGeneration: null);

	internal Task NotifyAsync(string method, object parameters, long expectedGeneration) =>
		NotifyAsync(method, parameters, (long?)expectedGeneration);

	private Task NotifyAsync(string method, object parameters, long? expectedGeneration) {
		ArgumentException.ThrowIfNullOrEmpty(method);
		ArgumentNullException.ThrowIfNull(parameters);
		return WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, expectedGeneration);
	}

	/// <summary>Returns a successful response to an agent request.</summary>
	public Task RespondAsync(AcpClientRequest request, object result) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(result);
		return WriteRawResponseAsync(request, result, error: null);
	}

	/// <summary>Returns an error response to an agent request.</summary>
	public Task RespondErrorAsync(AcpClientRequest request, int code, string message, object? data) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrEmpty(message);
		return WriteRawResponseAsync(request, result: null, new { code, message, data });
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		lock (_processGate) {
			if (_disposed) {
				return ValueTask.CompletedTask;
			}
			_disposed = true;
		}
		_supervisor.Dispose();
		FailPending(new ObjectDisposedException(nameof(AcpJsonRpcConnection)));
		return ValueTask.CompletedTask;
	}

	private void CancelRequest(long id, CancellationToken ct) {
		if (!_pending.TryRemove(id, out var pending)) {
			return;
		}
		_cancelled.TryAdd(id, 0);
		_ = SendCancellationAsync(id, pending.Generation);
		pending.Completion.TrySetCanceled(ct);
	}

	private async Task SendCancellationAsync(long id, long generation) {
		try {
			await NotifyAsync("$/cancel_request", new { requestId = id }, generation).ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
			_log($"[acp:{_providerId}] request cancellation could not be sent: {ex.Message}");
		}
	}

	private void StopProcess() {
		OwnedProcess? process;
		lock (_deliveryGate) {
			lock (_processGate) {
				process = _process;
				_process = null;
				_processGeneration = 0;
			}
		}
		if (process is null) {
			return;
		}
		FailPending(new IOException("ACP agent stopped."));
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) {
			_log($"[acp:{_providerId}] stop failed: {ex.Message}");
		} finally {
			process.Dispose();
		}
	}

	private Task WriteAsync(object value, long? expectedGeneration) =>
		WriteLineAsync(JsonSerializer.Serialize(value), expectedGeneration);

	private async Task WriteRequestAsync(
		long id,
		string method,
		object parameters,
		long? expectedGeneration,
		TaskCompletionSource<JsonElement> completion) {
		string line = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
		await _writeGate.WaitAsync().ConfigureAwait(false);
		try {
			OwnedProcess process;
			lock (_deliveryGate) {
				process = RunningProcess(out long generation);
				if (expectedGeneration is { } expected && generation != expected) {
					throw new InvalidOperationException("The ACP operation belongs to a previous process generation.");
				}
				if (!_pending.TryAdd(id, new PendingRequest(generation, completion))) {
					throw new InvalidOperationException($"ACP request id {id} is already pending.");
				}
			}
			try {
				await WriteLineAsync(process, line).ConfigureAwait(false);
			} catch {
				_pending.TryRemove(id, out _);
				throw;
			}
		} finally {
			_writeGate.Release();
		}
	}

	private async Task WriteRawResponseAsync(AcpClientRequest request, object? result, object? error) {
		var payload = new Dictionary<string, object?> {
			["jsonrpc"] = "2.0",
			["id"] = request.ResponseId,
		};
		payload[error is null ? "result" : "error"] = error ?? result;
		string line = JsonSerializer.Serialize(payload);
		await _writeGate.WaitAsync().ConfigureAwait(false);
		try {
			OwnedProcess process;
			lock (_deliveryGate) {
				process = RunningProcess(out long generation);
				if (generation != request.Generation) {
					throw new InvalidOperationException("The ACP request belongs to a previous process generation.");
				}
			}
			await WriteLineAsync(process, line).ConfigureAwait(false);
		} finally {
			_writeGate.Release();
		}
	}

	private async Task WriteLineAsync(string line, long? expectedGeneration) {
		await _writeGate.WaitAsync().ConfigureAwait(false);
		try {
			OwnedProcess process;
			lock (_deliveryGate) {
				process = RunningProcess(out long generation);
				if (expectedGeneration is { } expected && generation != expected) {
					throw new InvalidOperationException("The ACP operation belongs to a previous process generation.");
				}
			}
			await WriteLineAsync(process, line).ConfigureAwait(false);
		} finally {
			_writeGate.Release();
		}
	}

	private static async Task WriteLineAsync(OwnedProcess process, string line) {
		await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
		await process.StandardInput.FlushAsync().ConfigureAwait(false);
	}

	private OwnedProcess RunningProcess(out long generation) {
		lock (_processGate) {
			if (_disposed || _process is null || _process.HasExited) {
				throw new InvalidOperationException("ACP agent is not running.");
			}
			generation = _processGeneration;
			if (Volatile.Read(ref _faultedGeneration) >= generation) {
				throw new InvalidOperationException("The ACP agent generation has failed.");
			}
			return _process;
		}
	}

	private void FailPending(Exception error) {
		foreach (long id in _pending.Keys) {
			if (_pending.TryRemove(id, out var request)) {
				request.Completion.TrySetException(error);
			}
		}
	}

	private void FailPending(long generation, Exception error) {
		foreach (var entry in _pending) {
			if (entry.Value.Generation == generation
				&& _pending.TryRemove(entry.Key, out var request)) {
				request.Completion.TrySetException(error);
			}
		}
	}

	private void SignalProtocolFault(long generation, Exception error, bool reportUnhealthy) {
		bool accepted;
		lock (_deliveryGate) accepted = ClaimProtocolFaultSerialized(generation);
		if (accepted) PublishProtocolFault(generation, error, reportUnhealthy);
	}

	private bool ClaimProtocolFaultSerialized(long generation) {
		lock (_processGate) if (_processGeneration != generation) return false;
		while (true) {
			long previous = Volatile.Read(ref _faultedGeneration);
			if (previous >= generation) return false;
			if (Interlocked.CompareExchange(ref _faultedGeneration, generation, previous) == previous) break;
		}
		return true;
	}

	private void PublishProtocolFault(long generation, Exception error, bool reportUnhealthy) {
		FailPending(generation, error);
		ProtocolFaulted?.Invoke(generation, error);
		if (reportUnhealthy) _supervisor.ReportUnhealthy(generation, error.Message);
	}

	internal static string CanonicalId(JsonElement id) => id.ValueKind switch {
		JsonValueKind.String => "s:" + (id.GetString() ?? string.Empty),
		JsonValueKind.Number => "n:" + id.GetRawText(),
		_ => throw new AcpProtocolException("ACP request ids must be strings or numbers."),
	};

	private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

	private static int ReadExitCode(OwnedProcess process) {
		try {
			return process.ExitCode;
		} catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) {
			return -1;
		}
	}

	private sealed record PendingRequest(long Generation, TaskCompletionSource<JsonElement> Completion);
}
