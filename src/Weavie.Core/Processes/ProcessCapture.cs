using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Weavie.Core.Processes;

/// <summary>
/// Runs a child process to completion and captures its output. <see cref="ProcessSupervisor"/> owns long-lived
/// children; this owns the other case — one short run whose exit code and output are the whole answer.
/// <para>
/// It owns the pipe discipline no call site should have to repeat: stdin, stdout and stderr are all serviced
/// concurrently for the child's whole life, so a child that fills an OS pipe buffer can never block on a write
/// nobody is reading. It reports rather than judges: a start failure and a non-zero exit both come back in
/// <see cref="ProcessCaptureResult"/> for the caller to translate.
/// </para>
/// </summary>
public static class ProcessCapture {
	// Encoding.UTF8 carries a BOM preamble, which Process flushes into the child's stdin the moment it starts —
	// corrupting the first line the child reads, and breaking the pipe outright when it has already exited.
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// Runs <paramref name="request"/> to completion. If <paramref name="ct"/> fires first the child's process tree
	/// is killed and a <see cref="ProcessCanceledException"/> carries out what it had printed.
	/// </summary>
	public static async Task<ProcessCaptureResult> RunAsync(ProcessCaptureRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		using var process = new Process { StartInfo = StartInfo(request) };
		try {
			process.Start();
		} catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
			return new ProcessCaptureResult(-1, string.Empty, string.Empty, ex);
		}

		// None of the three pipes takes the caller's token: a pipe read is not interruptible, so killing the child
		// is what closes them and ends the reads. Draining stdout and stderr together is the point of this type —
		// reading one at a time, or only after the child exits, deadlocks as soon as the other's buffer fills.
		var stdin = FeedAsync(process.StandardInput, request.StandardInput);
		var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
		try {
			await process.WaitForExitAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			KillTree(process);
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			await stdin.ConfigureAwait(false);
			throw new ProcessCanceledException(
				await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), ct);
		}

		await stdin.ConfigureAwait(false);
		return new ProcessCaptureResult(
			process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), null);
	}

	/// <summary>Blocking <see cref="RunAsync"/>, for the synchronous call sites that own a child end to end.</summary>
	public static ProcessCaptureResult Run(ProcessCaptureRequest request, CancellationToken ct) =>
		RunAsync(request, ct).GetAwaiter().GetResult();

	private static ProcessStartInfo StartInfo(ProcessCaptureRequest request) {
		var info = new ProcessStartInfo {
			FileName = request.FileName,
			WorkingDirectory = request.WorkingDirectory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			// Every tool Weavie captures speaks UTF-8; the Windows console codepage would mangle it.
			StandardInputEncoding = Utf8,
			StandardOutputEncoding = Utf8,
			StandardErrorEncoding = Utf8,
		};
		foreach (string argument in request.Arguments) {
			info.ArgumentList.Add(argument);
		}

		foreach (var (name, value) in request.Environment) {
			info.Environment[name] = value;
		}

		return info;
	}

	// A child that exits before its input lands leaves a broken pipe, and it is already gone: its exit code and
	// output are the answer, not the failed write.
	private static async Task FeedAsync(StreamWriter stdin, string content) {
		try {
			await stdin.WriteAsync(content).ConfigureAwait(false);
			stdin.Close();
		} catch (IOException) {
		}
	}

	// The child can exit between the check and the kill — that race produced the outcome we wanted anyway.
	private static void KillTree(Process process) {
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) {
		}
	}
}
