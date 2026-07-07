using System.Diagnostics;
using System.Text.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Supervises <c>codex app-server --stdio</c> process launch and stdio pumps.</summary>
public sealed partial class CodexAppServerClient {
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
		foreach (string argument in _globalArguments) {
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.StartInfo.ArgumentList.Add("app-server");
		foreach (string argument in _configArguments) {
			process.StartInfo.ArgumentList.Add(argument);
		}

		foreach (string argument in _appServerArguments) {
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.StartInfo.ArgumentList.Add("--stdio");
		foreach (var (name, value) in _environment) {
			process.StartInfo.Environment[name] = value;
		}

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

	private static int ReadExitCode(Process process) {
		try {
			return process.ExitCode;
		} catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) {
			return -1;
		}
	}
}
