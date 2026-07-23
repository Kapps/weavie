using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Weavie.Core.Processes;

/// <summary>One tool invocation: the executable, its argument list, extra environment, and working directory.</summary>
/// <param name="FileName">The executable to launch.</param>
/// <param name="Arguments">Arguments passed as a list — no shell, so paths never need quoting.</param>
/// <param name="Environment">Extra environment variables layered over the inherited ones.</param>
/// <param name="WorkingDirectory">The directory the tool runs in.</param>
public sealed record ToolProcessRequest(
	string FileName,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment,
	string WorkingDirectory);

/// <summary>The captured outcome of a one-shot tool run; a tool that couldn't start reports exit -1.</summary>
/// <param name="ExitCode">The process exit code (0 = success).</param>
/// <param name="StdOut">Captured standard output.</param>
/// <param name="StdErr">Captured standard error.</param>
public sealed record ToolProcessResult(int ExitCode, string StdOut, string StdErr) {
	/// <summary>Both streams joined for logging, skipping whichever is blank.</summary>
	public string CombinedOutput => string.Join(
		System.Environment.NewLine,
		new[] { StdOut, StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Runs a one-shot tool to completion with its output captured. A transient helper, exempt from
/// <c>ProcessSupervisor</c>; a tool that cannot start reports as a failed run, never a throw.
/// </summary>
public static class ToolProcess {
	/// <summary>Runs <paramref name="request"/> to completion, capturing output.</summary>
	public static async Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);

		var info = new ProcessStartInfo {
			FileName = request.FileName,
			WorkingDirectory = request.WorkingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (string arg in request.Arguments) {
			info.ArgumentList.Add(arg);
		}

		foreach (var (key, value) in request.Environment) {
			info.Environment[key] = value;
		}

		using var process = new Process { StartInfo = info };
		try {
			process.Start();
		} catch (Win32Exception ex) {
			return new ToolProcessResult(-1, "", $"Unable to start '{request.FileName}': {ex.Message}");
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
		var stderrTask = process.StandardError.ReadToEndAsync(ct);
		try {
			await process.WaitForExitAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// Cancellation only stops the wait — reap the whole tree so no detached tool keeps running/writing.
			try {
				process.Kill(entireProcessTree: true);
			} catch (InvalidOperationException) {
				// Already exited between the cancel and the kill.
			} catch (Win32Exception) {
				// Terminating or access denied — nothing more we can do; the wait is already abandoned.
			}

			throw;
		}

		return new ToolProcessResult(
			process.ExitCode,
			await stdoutTask.ConfigureAwait(false),
			await stderrTask.ConfigureAwait(false));
	}
}
