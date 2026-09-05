using System.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.WorktreeServe;

internal static class TransientCommand {
	// Streams the child's output straight to this console instead of capturing it, so a long provisioning step
	// reports progress while it runs.
	public static async Task RunAsync(
		string executable,
		IReadOnlyList<string> args,
		string workingDirectory,
		IReadOnlyDictionary<string, string> environment,
		CancellationToken cancellationToken) {
		var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		foreach (var (name, value) in environment) {
			info.Environment[name] = value;
		}

		using var process = new Process { StartInfo = info };
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
		var result = await ProcessCapture.RunAsync(
			new ProcessCaptureRequest { FileName = executable, Arguments = args, WorkingDirectory = workingDirectory },
			cancellationToken).ConfigureAwait(false);
		// Every captured command here is a precondition of serving the worktree, so neither outcome is recoverable.
		if (result.StartFailure is { } failure) {
			throw new InvalidOperationException($"'{executable} {string.Join(' ', args)}' could not start.", failure);
		}

		if (result.ExitCode != 0) {
			throw new InvalidOperationException(
				$"'{executable} {string.Join(' ', args)}' exited with code {result.ExitCode}: {result.StdErr.Trim()}");
		}

		return result.StdOut.Trim();
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
