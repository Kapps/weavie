using Weavie.Core.Processes;

namespace Weavie.Core.Lsp;

/// <summary>The outcome of an install: whether the server now resolves, and the toast-ready message.</summary>
/// <param name="Ok">Whether the install succeeded AND the server resolves.</param>
/// <param name="Message">The user-facing summary (a failure carries the output tail inline).</param>
public sealed record ServerInstallResult(bool Ok, string Message);

/// <summary>
/// Runs a <see cref="ServerInstallOffer"/>'s canonical install into Weavie's own tools folder
/// (<see cref="WeaviePaths.Tools"/>) — deterministic, zero model tokens, never the user's global toolset —
/// then verifies the server actually resolves. Full output goes to the log sink; the returned message is the
/// user-facing surface.
/// </summary>
public sealed class LanguageServerInstaller {
	private readonly Func<string, string?> _findOnPath;
	private readonly Func<LanguageServerDescriptor, ResolvedCommand?> _resolve;
	private readonly Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> _run;
	private readonly Action<string> _log;

	/// <summary>Creates the installer over its seams (<see cref="ServerResolver"/>/<see cref="ToolProcess"/> in production, fakes in tests).</summary>
	/// <param name="findOnPath">Locates the recipe's toolchain on <c>PATH</c>.</param>
	/// <param name="resolve">Re-resolves the descriptor after the install to verify it took.</param>
	/// <param name="run">Runs the install process to completion.</param>
	/// <param name="log">Diagnostic sink for the full install output.</param>
	public LanguageServerInstaller(
		Func<string, string?> findOnPath,
		Func<LanguageServerDescriptor, ResolvedCommand?> resolve,
		Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> run,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(findOnPath);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(run);
		ArgumentNullException.ThrowIfNull(log);
		_findOnPath = findOnPath;
		_resolve = resolve;
		_run = run;
		_log = log;
	}

	/// <summary>Installs <paramref name="offer"/>'s candidate and verifies it resolves.</summary>
	/// <param name="offer">The offer to fulfill.</param>
	/// <param name="workingDirectory">The directory the toolchain runs in.</param>
	/// <param name="ct">Cancels the install run.</param>
	public async Task<ServerInstallResult> InstallAsync(ServerInstallOffer offer, string workingDirectory, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(offer);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

		string? toolchainPath = _findOnPath(offer.Recipe.Toolchain);
		if (toolchainPath is null) {
			return new ServerInstallResult(false,
				$"Can't install {offer.Candidate.Command}: '{offer.Recipe.Toolchain}' is not on PATH.");
		}

		// BuildCommand wraps Windows .cmd shims (npm.cmd) through cmd.exe, exactly like server launches.
		var command = ServerResolver.BuildCommand(toolchainPath, ToolchainInstall.Arguments(offer.Recipe));
		var result = await _run(new ToolProcessRequest(
			command.FileName, command.Arguments, ToolchainInstall.Environment(offer.Recipe), workingDirectory), ct)
			.ConfigureAwait(false);

		string output = string.Join(
			Environment.NewLine,
			new[] { result.StdOut, result.StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
		_log($"[lsp-install] {offer.Recipe.Toolchain} install of {offer.Recipe.Package} exited {result.ExitCode}"
			+ (output.Length > 0 ? Environment.NewLine + output : ""));

		if (result.ExitCode != 0) {
			return new ServerInstallResult(false,
				$"Installing {offer.Candidate.Command} failed (exit {result.ExitCode}): {Tail(result)}");
		}

		if (_resolve(offer.Descriptor) is null) {
			return new ServerInstallResult(false,
				$"Installed {offer.Candidate.Command}, but no server appeared in {ToolchainInstall.BinDir(offer.Recipe.Toolchain)} — see View Logs for the install output.");
		}

		return new ServerInstallResult(true,
			$"Installed {offer.Candidate.Command} — {offer.Descriptor.DisplayName} language support is ready.");
	}

	// The last lines of what the tool said (stderr, else stdout), compact enough for a toast.
	private static string Tail(ToolProcessResult result) {
		string text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
		string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length == 0) {
			return "no output";
		}

		string tail = string.Join(" · ", lines[^Math.Min(2, lines.Length)..]);
		return tail.Length > 240 ? tail[^240..] : tail;
	}
}
