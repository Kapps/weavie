using Weavie.Core.Processes;

namespace Weavie.Core.Remote;

/// <summary>The outcome of one captured <c>tailscale</c> invocation.</summary>
public readonly record struct TailscaleResult(int ExitCode, string Stdout, string Stderr);

/// <summary>The shared seam for invoking the Tailscale CLI.</summary>
public interface ITailscaleCli {
	/// <summary>The resolved executable used for foreground Tailscale processes.</summary>
	string Executable { get; }

	/// <summary>Environment required when launching the resolved executable.</summary>
	IReadOnlyDictionary<string, string> ProcessEnvironment { get; }

	/// <summary>Runs <c>tailscale</c> with <paramref name="args"/>, returning its exit code and captured output.</summary>
	TailscaleResult Run(IReadOnlyList<string> args);
}

/// <summary>Shells out to the real <c>tailscale</c> executable, resolving its install location.</summary>
public sealed class TailscaleCli : ITailscaleCli {
	// Serve can wait for tailnet approval indefinitely; this bound preserves its captured explanation and fails loudly.
	private const int TimeoutMs = 20_000;

	/// <summary>Creates a CLI using the platform's installed Tailscale executable.</summary>
	public TailscaleCli() {
		Executable = ResolveExecutable();
		ProcessEnvironment = OperatingSystem.IsMacOS()
			? new Dictionary<string, string> { ["TAILSCALE_BE_CLI"] = "1" }
			: [];
	}

	/// <inheritdoc/>
	public string Executable { get; }

	/// <inheritdoc/>
	public IReadOnlyDictionary<string, string> ProcessEnvironment { get; }

	/// <inheritdoc/>
	public TailscaleResult Run(IReadOnlyList<string> args) {
		ArgumentNullException.ThrowIfNull(args);
		using var deadline = new CancellationTokenSource(TimeoutMs);
		try {
			var result = ProcessCapture.Run(
				new ProcessCaptureRequest {
					FileName = Executable,
					Arguments = args,
					Environment = ProcessEnvironment,
				},
				deadline.Token);
			return result.StartFailure is { } failure
				? throw new InvalidOperationException(
					$"could not run the Tailscale CLI ('{Executable}') — is Tailscale installed?", failure)
				: new TailscaleResult(result.ExitCode, result.StdOut, result.StdErr);
		} catch (ProcessCanceledException ex) {
			string captured = $"{ex.StdErr}{ex.StdOut}".Trim();
			throw new InvalidOperationException(
				$"'tailscale {string.Join(' ', args)}' did not return within {TimeoutMs / 1000}s — it may be waiting on a tailnet setting or input."
				+ (captured.Length == 0 ? string.Empty : $"\n{captured}"));
		}
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
		if (OperatingSystem.IsMacOS()) {
			const string applicationCli = "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
			if (File.Exists(applicationCli)) {
				return applicationCli;
			}
		}

		return "tailscale";
	}
}
