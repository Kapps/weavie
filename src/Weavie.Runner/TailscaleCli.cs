using System.Diagnostics;

namespace Weavie.Runner;

/// <summary>The outcome of one <c>tailscale</c> invocation.</summary>
internal readonly record struct TailscaleResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The seam for invoking the <c>tailscale</c> CLI, so <see cref="TailscaleServeFront"/> is unit-testable against
/// a scripted fake with no real daemon.
/// </summary>
internal interface ITailscaleCli {
	/// <summary>Runs <c>tailscale</c> with <paramref name="args"/>, returning its exit code and captured output.</summary>
	TailscaleResult Run(IReadOnlyList<string> args);
}

/// <summary>Shells out to the real <c>tailscale</c> executable, resolving its install location.</summary>
internal sealed class TailscaleCli : ITailscaleCli {
	// A bound that fails LOUDLY, not a silent cap: `tailscale serve` blocks indefinitely when Serve isn't enabled
	// on the tailnet (it prints an enable URL and waits), which would otherwise freeze the runner with no output.
	// The config calls are sub-second when healthy, so a generous ceiling only ever trips on a real block — and
	// then surfaces the CLI's own message instead of hanging.
	private const int TimeoutMs = 20_000;

	// The Windows installer registers tailscale.exe via an App Paths entry, NOT the PATH — which `cmd`/ShellExecute
	// honor but Process.Start(UseShellExecute=false) (needed to capture output) does not. So resolve the known
	// install location, falling back to the bare name (PATH) on other platforms or if it is on PATH.
	private static readonly string Executable = ResolveExecutable();

	/// <inheritdoc/>
	public TailscaleResult Run(IReadOnlyList<string> args) {
		ArgumentNullException.ThrowIfNull(args);
		var info = new ProcessStartInfo(Executable) {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		Process process;
		try {
			process = Process.Start(info) ?? throw new InvalidOperationException("tailscale did not start.");
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			throw new InvalidOperationException($"could not run the Tailscale CLI ('{Executable}') — is Tailscale installed?", ex);
		}

		// Drain both pipes concurrently so a child that writes to stdout and stderr can't deadlock the reader.
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(TimeoutMs)) {
			try {
				process.Kill(entireProcessTree: true);
			} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
				// Already gone; nothing to kill.
			}

			// The kill closes the pipes, so the reads now complete with whatever the CLI emitted before blocking
			// (e.g. the "Serve is not enabled on your tailnet … enable at <url>" notice) — surface it.
			string captured = $"{stderr.GetAwaiter().GetResult()}{stdout.GetAwaiter().GetResult()}".Trim();
			throw new InvalidOperationException(
				$"'tailscale {string.Join(' ', args)}' did not return within {TimeoutMs / 1000}s — it may be waiting on a tailnet setting or input."
				+ (captured.Length == 0 ? string.Empty : $"\n{captured}"));
		}

		return new TailscaleResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
	}

	private static string ResolveExecutable() {
		if (OperatingSystem.IsWindows()) {
			string[] candidates = [
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
			];
			foreach (string candidate in candidates) {
				if (File.Exists(candidate)) {
					return candidate;
				}
			}
		}

		return "tailscale"; // PATH — the normal case on Linux/macOS, or when it is on PATH on Windows.
	}
}
