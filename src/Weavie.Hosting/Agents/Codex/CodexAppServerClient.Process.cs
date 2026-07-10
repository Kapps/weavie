using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Supervises <c>codex app-server --stdio</c> process launch and stdio pumps.</summary>
public sealed partial class CodexAppServerClient {
	private static readonly Encoding StdioEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private void StartProcess(SupervisedLaunch launch) {
		var process = new Process {
			StartInfo = StartInfo(
				_launch,
				_globalArguments,
				_configArguments,
				_appServerArguments,
				_environment),
			EnableRaisingEvents = true,
		};

		process.Exited += (_, _) => {
			int exitCode = ReadExitCode(process);
			_log($"[codex-app-server] exited {exitCode}");
			FailPending(new IOException($"Codex app-server exited with code {exitCode}."));
			launch.NotifyExited(exitCode);
		};
		if (!process.Start()) {
			throw new InvalidOperationException("Codex app-server did not start.");
		}

		lock (_gate) {
			_process = process;
		}

		ProcessStarted?.Invoke(launch.Attempt);
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

	internal static ProcessStartInfo StartInfo(
		CodexAppServerLaunch launch,
		IReadOnlyList<string> globalArguments,
		IReadOnlyList<string> configArguments,
		IReadOnlyList<string> appServerArguments,
		IReadOnlyDictionary<string, string> environment) {
		ArgumentNullException.ThrowIfNull(launch);
		ArgumentNullException.ThrowIfNull(globalArguments);
		ArgumentNullException.ThrowIfNull(configArguments);
		ArgumentNullException.ThrowIfNull(appServerArguments);
		ArgumentNullException.ThrowIfNull(environment);
		ProcessStartInfo info = new(launch.Command) {
			WorkingDirectory = launch.WorkingDirectory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = StdioEncoding,
			StandardOutputEncoding = StdioEncoding,
			StandardErrorEncoding = StdioEncoding,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		foreach (string argument in globalArguments) {
			info.ArgumentList.Add(argument);
		}

		info.ArgumentList.Add("app-server");
		foreach (string argument in configArguments) {
			info.ArgumentList.Add(argument);
		}

		foreach (string argument in appServerArguments) {
			info.ArgumentList.Add(argument);
		}

		info.ArgumentList.Add("--stdio");
		foreach (var entry in environment) {
			string name = entry.Key;
			string value = entry.Value;
			info.Environment[name] = value;
		}

		PrependPath(info, launch.PathEntries);

		return info;
	}

	private static void PrependPath(ProcessStartInfo info, IReadOnlyList<string> entries) {
		if (entries.Count == 0) {
			return;
		}

		string key = PathKey(info.Environment);
		string existing = info.Environment.TryGetValue(key, out string? path) && path is not null
			? path
			: Environment.GetEnvironmentVariable(key) ?? string.Empty;
		var parts = entries.Where(entry => entry.Length > 0).ToList();
		if (existing.Length > 0) {
			parts.Add(existing);
		}

		info.Environment[key] = string.Join(Path.PathSeparator, parts);
	}

	private static string PathKey(IDictionary<string, string?> environment) {
		foreach (string key in environment.Keys) {
			if (string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase)) {
				return key;
			}
		}

		return "PATH";
	}

	private async Task ReadStdoutAsync(Process process) {
		try {
			while (!process.HasExited && await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
				try {
					HandleLine(line);
				} catch (JsonException ex) {
					// A stray non-JSON line (runtime warning, update banner) must not kill the pump — that would
					// leave the live process's responses unread and hang every later request forever.
					_log($"[codex-app-server] ignored non-JSON stdout line: {ex.Message}");
				}
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
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
