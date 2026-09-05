using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.AgentClientProtocol;

internal sealed class AcpTerminalManager : IAsyncDisposable {
	private readonly string _workspace;
	private readonly Action<string> _log;
	private readonly ConcurrentDictionary<string, OwnedTerminal> _terminals = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, AcpTerminalOutput> _released = new(StringComparer.Ordinal);
	private long _nextId;

	public AcpTerminalManager(string workspace, Action<string> log) {
		_workspace = workspace;
		_log = log;
	}

	public async Task<string> CreateAsync(JsonElement parameters, long generation, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
		string command = RequiredString(parameters, "command");
		string cwd = parameters.TryGetProperty("cwd", out var cwdValue)
			? cwdValue.ValueKind == JsonValueKind.Null
				? _workspace
				: cwdValue.ValueKind == JsonValueKind.String && cwdValue.GetString() is { } requestedCwd
				? requestedCwd
				: throw new AcpProtocolException("ACP terminal cwd must be a string.")
			: _workspace;
		cwd = Path.GetFullPath(cwd, _workspace);
		if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"ACP terminal cwd does not exist: {cwd}");
		string[] arguments = parameters.TryGetProperty("args", out var args)
			? args.ValueKind == JsonValueKind.Array
				? [.. args.EnumerateArray().Select(ReadArgument)]
				: throw new AcpProtocolException("ACP terminal args must be an array.")
			: [];
		var environment = parameters.TryGetProperty("env", out var env)
			? env.ValueKind == JsonValueKind.Array
				? env.EnumerateArray().ToDictionary(
				entry => RequiredString(entry, "name"),
				entry => RequiredString(entry, "value"),
				StringComparer.Ordinal)
				: throw new AcpProtocolException("ACP terminal env must be an array.")
			: new Dictionary<string, string>(StringComparer.Ordinal);
		long? limit = ReadOutputLimit(parameters);
		var terminal = new AcpTerminal(id, command, arguments, cwd, environment, limit, _log);
		if (!_terminals.TryAdd(id, new OwnedTerminal(generation, terminal))) {
			throw new InvalidOperationException($"ACP terminal id '{id}' is already in use.");
		}
		try {
			await terminal.StartAsync(ct).ConfigureAwait(false);
			return id;
		} catch {
			_terminals.TryRemove(id, out _);
			await terminal.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public AcpTerminalOutput Output(string id) => TryOutput(id, out var output)
		? output
		: throw new KeyNotFoundException($"ACP terminal '{id}' does not exist.");

	/// <summary>Reports the output of a client-created terminal; agent-owned ids are simply unknown here.</summary>
	public bool TryOutput(string id, out AcpTerminalOutput output) {
		if (_terminals.TryGetValue(id, out var owned)) {
			output = owned.Terminal.Output();
			return true;
		}
		return _released.TryGetValue(id, out output!);
	}

	public Task<AcpTerminalExit> WaitAsync(string id, CancellationToken ct) => Resolve(id).WaitAsync(ct);

	public void Kill(string id) => Resolve(id).Kill();

	public async Task ReleaseAsync(string id, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		if (!_terminals.TryRemove(id, out var owned)) {
			throw new KeyNotFoundException($"ACP terminal '{id}' does not exist.");
		}
		_released[id] = owned.Terminal.Output();
		await owned.Terminal.DisposeAsync().ConfigureAwait(false);
	}

	public void ReleaseGeneration(long generation) {
		foreach (var entry in _terminals.Where(entry => entry.Value.Generation == generation).ToArray()) {
			if (_terminals.TryRemove(entry.Key, out var owned)) {
				_released[entry.Key] = owned.Terminal.Output();
				owned.Terminal.DisposeAsync().AsTask().GetAwaiter().GetResult();
			}
		}
	}

	public async ValueTask DisposeAsync() {
		var terminals = _terminals.Values.Select(value => value.Terminal).ToArray();
		_terminals.Clear();
		_released.Clear();
		foreach (var terminal in terminals) {
			await terminal.DisposeAsync().ConfigureAwait(false);
		}
	}

	private AcpTerminal Resolve(string id) => _terminals.TryGetValue(id, out var owned)
		? owned.Terminal
		: throw new KeyNotFoundException($"ACP terminal '{id}' does not exist.");

	private sealed record OwnedTerminal(long Generation, AcpTerminal Terminal);

	private static string RequiredString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { } text
				? text
				: throw new AcpProtocolException($"ACP terminal request is missing '{property}'.");

	private static string ReadArgument(JsonElement value) => value.ValueKind == JsonValueKind.String
		? value.GetString() ?? string.Empty
		: throw new AcpProtocolException("ACP terminal arguments must be strings.");

	private static long? ReadOutputLimit(JsonElement parameters) {
		if (!parameters.TryGetProperty("outputByteLimit", out var value) || value.ValueKind == JsonValueKind.Null) {
			return null;
		}
		if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result < 0) {
			throw new AcpProtocolException("ACP terminal outputByteLimit must be a non-negative integer.");
		}
		return result;
	}
}

internal sealed class AcpTerminal : IAsyncDisposable {
	private static readonly Encoding Utf8 = new UTF8Encoding(false);
	private readonly string _id;
	private readonly string _command;
	private readonly IReadOnlyList<string> _arguments;
	private readonly string _cwd;
	private readonly IReadOnlyDictionary<string, string> _environment;
	private readonly long? _limit;
	private readonly Action<string> _log;
	private readonly Lock _gate = new();
	private readonly StringBuilder _output = new();
	private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<AcpTerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly ProcessSupervisor _supervisor;
	private OwnedProcess? _process;
	private bool _truncated;

	public AcpTerminal(
		string id,
		string command,
		IReadOnlyList<string> arguments,
		string cwd,
		IReadOnlyDictionary<string, string> environment,
		long? limit,
		Action<string> log) {
		_id = id;
		_command = command;
		_arguments = arguments;
		_cwd = cwd;
		_environment = environment;
		_limit = limit;
		_log = log;
		_supervisor = new ProcessSupervisor(
			$"acp-terminal:{id}",
			StartProcess,
			StopProcess,
			new SupervisionOptions { Policy = RestartPolicy.Never },
			entry => log($"[acp-terminal:{id}] {entry.Level}: {entry.Message}"),
			new SystemSupervisorClock());
	}

	public async Task StartAsync(CancellationToken ct) {
		_supervisor.Start();
		await _started.Task.WaitAsync(ct).ConfigureAwait(false);
	}

	public AcpTerminalOutput Output() {
		lock (_gate) {
			return new AcpTerminalOutput(
				_output.ToString(),
				_truncated,
				_exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null);
		}
	}

	public Task<AcpTerminalExit> WaitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

	public void Kill() {
		OwnedProcess? process;
		lock (_gate) {
			process = _process;
		}
		if (process is { HasExited: false }) {
			process.Kill(entireProcessTree: true);
		}
	}

	public ValueTask DisposeAsync() {
		_supervisor.Dispose();
		_started.TrySetException(new ObjectDisposedException(nameof(AcpTerminal)));
		return ValueTask.CompletedTask;
	}

	private void StartProcess(SupervisedLaunch launch) {
		try {
			StartProcessCore(launch);
		} catch (Exception ex) {
			_started.TrySetException(ex);
			throw;
		}
	}

	private void StartProcessCore(SupervisedLaunch launch) {
		var processExited = new TaskCompletionSource<AcpTerminalExit>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var info = new ProcessStartInfo(_command) {
			WorkingDirectory = _cwd,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Utf8,
			StandardErrorEncoding = Utf8,
		};
		foreach (string argument in _arguments) info.ArgumentList.Add(argument);
		foreach (var entry in _environment) info.Environment[entry.Key] = entry.Value;
		var process = OwnedProcess.Start(info);
		lock (_gate) {
			_process = process;
		}
		_ = process.ObserveExitAsync(exitCode => {
			var status = new AcpTerminalExit(exitCode, null);
			processExited.TrySetResult(status);
			launch.NotifyExited(exitCode);
		});
		_started.TrySetResult();
		var stdout = ReadAsync(process.StandardOutput);
		var stderr = ReadAsync(process.StandardError);
		_ = CompleteExitAsync(processExited.Task, stdout, stderr);
	}

	private async Task CompleteExitAsync(
		Task<AcpTerminalExit> processExited,
		Task stdout,
		Task stderr) {
		try {
			var status = await processExited.ConfigureAwait(false);
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
			_exit.TrySetResult(status);
		} catch (Exception ex) {
			_exit.TrySetException(ex);
		}
	}

	private async Task ReadAsync(StreamReader reader) {
		char[] buffer = new char[4096];
		try {
			while (await reader.ReadAsync(buffer).ConfigureAwait(false) is > 0 and var read) {
				Append(new string(buffer, 0, read));
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			_log($"[acp-terminal:{_id}] output closed: {ex.Message}");
		}
	}

	private void Append(string text) {
		lock (_gate) {
			_output.Append(text);
			if (_limit is not { } limit || Utf8.GetByteCount(_output.ToString()) <= limit) {
				return;
			}
			string value = _output.ToString();
			int low = 0;
			int high = value.Length;
			while (low < high) {
				int middle = low + (high - low) / 2;
				if (Utf8.GetByteCount(value.AsSpan(middle)) <= limit) high = middle;
				else low = middle + 1;
			}
			if (low < value.Length && low > 0 && char.IsLowSurrogate(value[low])) low++;
			_output.Clear();
			_output.Append(value.AsSpan(Math.Min(low, value.Length)));
			_truncated = true;
		}
	}

	private void StopProcess() {
		OwnedProcess? process;
		lock (_gate) {
			process = _process;
			_process = null;
		}
		if (process is null) return;
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
				process.WaitForExit();
			}
		} catch (InvalidOperationException) {
			// The process exited between the observation and kill.
		} finally {
			process.Dispose();
		}
	}
}

internal sealed record AcpTerminalOutput(string Output, bool Truncated, AcpTerminalExit? ExitStatus);

internal sealed record AcpTerminalExit(int? ExitCode, string? Signal);
