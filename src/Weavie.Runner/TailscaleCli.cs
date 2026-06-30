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

/// <summary>Shells out to the real <c>tailscale</c> executable on <c>PATH</c>.</summary>
internal sealed class TailscaleCli : ITailscaleCli {
	/// <inheritdoc/>
	public TailscaleResult Run(IReadOnlyList<string> args) {
		ArgumentNullException.ThrowIfNull(args);
		var info = new ProcessStartInfo("tailscale") {
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
			throw new InvalidOperationException("could not run 'tailscale' — is the Tailscale CLI installed and on PATH?", ex);
		}

		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new TailscaleResult(process.ExitCode, stdout, stderr);
	}
}
