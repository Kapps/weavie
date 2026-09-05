using System.Diagnostics;
using System.Text;
using Weavie.Core.Processes;
using Weavie.Hosting.Agents;

namespace Weavie.Hosting.Inference;

internal sealed record AgentCliProcessRequest {
	public required string Command { get; init; }

	public required string WorkingDirectory { get; init; }

	public required IReadOnlyList<string> Arguments { get; init; }

	public required IReadOnlyList<string> PathEntries { get; init; }

	public required IReadOnlyDictionary<string, string> Environment { get; init; }

	public required IReadOnlyList<string> RemoveEnvironment { get; init; }

	public required string StandardInput { get; init; }

	public required int MaxCapturedStdoutBytes { get; init; }

	public required bool CaptureStdout { get; init; }
}

internal sealed record AgentCliProcessResult(int ExitCode, string StandardOutput);

internal interface IAgentCliProcessRunner {
	Task<AgentCliProcessResult> RunAsync(AgentCliProcessRequest request, CancellationToken ct);
}

internal sealed class AgentCliOutputLimitException : IOException {
	public AgentCliOutputLimitException(int limit)
		: base($"The agent CLI wrote more than the allowed {limit} bytes to standard output.") { }
}

/// <summary>Runs one transient agent CLI process and kills its process tree on cancellation.</summary>
internal sealed class AgentCliProcessRunner : IAgentCliProcessRunner {
	public async Task<AgentCliProcessResult> RunAsync(AgentCliProcessRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaxCapturedStdoutBytes);
		using var process = OwnedProcess.Start(AgentCliProcessStartInfo.Create(
			request.Command, request.WorkingDirectory, request.Arguments, request.PathEntries,
			request.Environment, request.RemoveEnvironment));

		var stdout = ReadAsync(
			process.StandardOutput.BaseStream,
			request.MaxCapturedStdoutBytes,
			request.CaptureStdout,
			ct);
		var stderr = ReadAsync(process.StandardError.BaseStream, 1, capture: false, ct);
		try {
			try {
				await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), ct).ConfigureAwait(false);
				process.StandardInput.Close();
			} catch (IOException) when (!ct.IsCancellationRequested) {
				// The CLI can exit (and close its end of the pipe) before consuming stdin — e.g. an
				// immediate argument-validation failure. That's not a runner failure: the process's
				// real exit code and captured stdout below still apply.
			}

			var exited = process.WaitForExitAsync(ct);
			var first = await Task.WhenAny(exited, stdout, stderr).ConfigureAwait(false);
			if (first.IsFaulted || first.IsCanceled) {
				await first.ConfigureAwait(false);
			}
			await Task.WhenAll(exited, stdout, stderr).ConfigureAwait(false);
			return new AgentCliProcessResult(process.ExitCode, await stdout.ConfigureAwait(false));
		} catch {
			if (TryTerminate(process)) {
				await WaitForExitAsync(process).ConfigureAwait(false);
			}
			ct.ThrowIfCancellationRequested();
			throw;
		}
	}

	private static async Task<string> ReadAsync(Stream stream, int maxBytes, bool capture, CancellationToken ct) {
		using var output = capture ? new MemoryStream() : null;
		byte[] buffer = new byte[8192];
		while (true) {
			int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
			if (read == 0) {
				break;
			}
			if (capture && output!.Length + read > maxBytes) {
				throw new AgentCliOutputLimitException(maxBytes);
			}
			if (capture) {
				await output!.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
			}
		}

		return capture ? Encoding.UTF8.GetString(output!.ToArray()) : string.Empty;
	}

	private static bool TryTerminate(OwnedProcess process) {
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
			}
			return true;
		} catch (InvalidOperationException) when (process.HasExited) {
			return true;
		}
	}

	private static async Task WaitForExitAsync(OwnedProcess process) {
		try {
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
		} catch (InvalidOperationException) {
		}
	}
}
