using System.Diagnostics;

namespace Weavie.WorktreeServe;

internal static class TransientCommand {
	public static async Task RunAsync(
		string executable,
		IReadOnlyList<string> args,
		string workingDirectory,
		IReadOnlyDictionary<string, string> environment,
		CancellationToken cancellationToken) {
		using var process = Create(executable, args, workingDirectory, environment, capture: false);
		Console.WriteLine($"[worktree-serve] {executable} {string.Join(' ', args)}");
		process.Start();
		try {
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			Kill(process);
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			throw;
		}

		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"'{executable} {string.Join(' ', args)}' exited with code {process.ExitCode}.");
		}
	}

	public static async Task<string> CaptureAsync(
		string executable,
		IReadOnlyList<string> args,
		string workingDirectory,
		CancellationToken cancellationToken) {
		using var process = Create(executable, args, workingDirectory, new Dictionary<string, string>(), capture: true);
		process.Start();
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		try {
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			Kill(process);
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
			throw;
		}

		string output = await stdout.ConfigureAwait(false);
		string error = await stderr.ConfigureAwait(false);
		if (process.ExitCode != 0) {
			throw new InvalidOperationException(
				$"'{executable} {string.Join(' ', args)}' exited with code {process.ExitCode}: {error.Trim()}");
		}

		return output.Trim();
	}

	private static Process Create(
		string executable,
		IReadOnlyList<string> args,
		string workingDirectory,
		IReadOnlyDictionary<string, string> environment,
		bool capture) {
		var info = new ProcessStartInfo(executable) {
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = capture,
			RedirectStandardError = capture,
			UseShellExecute = false,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}
		foreach (var (name, value) in environment) {
			info.Environment[name] = value;
		}

		return new Process { StartInfo = info };
	}

	private static void Kill(Process process) {
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
		}
	}
}
