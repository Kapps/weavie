using Weavie.Core.Processes;

namespace Weavie.Hosting;

/// <summary>
/// Imports the user's login-shell environment into this process. A non-terminal launch (a macOS <c>.app</c> from
/// Finder, a Linux desktop entry, a headless host under a supervisor) inherits a minimal environment, so children
/// Weavie spawns directly (LSP servers, <c>git</c>) would otherwise miss <c>PATH</c> entries, <c>DOTNET_ROOT</c>,
/// and the like a terminal launch has.
/// </summary>
public static class LoginShellEnvironment {
	private const string Begin = "__WEAVIE_ENV_BEGIN__";
	private const string End = "__WEAVIE_ENV_END__";
	private const int ProbeSeconds = 5;

	// Transient session noise describing the probe subshell, not config worth propagating to children.
	private static readonly HashSet<string> Skip = new(StringComparer.Ordinal) { "_", "SHLVL", "PWD", "OLDPWD" };

	private static bool _imported;
	private static string _failure = string.Empty;

	/// <summary>Marks the import as already done, so a test host never spawns the developer's real shell.</summary>
	internal static void MarkImported() => _imported = true;

	/// <summary>
	/// Imports the login-shell environment on the first call (macOS/Linux); a no-op on Windows and on later calls.
	/// Returns a user-facing explanation of why the environment could not be read, empty when it was.
	/// </summary>
	/// <param name="log">Sink for a one-line note of what was imported.</param>
	public static async Task<string> ImportOnceAsync(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		if (_imported) {
			return _failure;
		}

		_imported = true;
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) {
			return _failure;
		}

		string? fenced = await ReadLoginShellEnvAsync().ConfigureAwait(false);
		if (string.IsNullOrEmpty(fenced)) {
			return _failure;
		}

		var imports = ResolveImports(ParseEnv(fenced));
		foreach (var (name, value) in imports) {
			Environment.SetEnvironmentVariable(name, value);
		}

		log($"imported login-shell environment ({imports.Count} vars)");
		return _failure;
	}

	/// <summary>Explains a probe whose fenced body never arrived — the shell never ran our command.</summary>
	internal static string HijackedMessage(string shell) =>
		$"Weavie could not read your shell environment: {shell} startup replaced the shell (an `exec` into another "
		+ "shell) before Weavie's probe ran, so anything launched through it may open that shell instead.";

	private static async Task<string?> ReadLoginShellEnvAsync() {
		string shell = LoginShell();
		// Bound the probe so a shell that never finishes its rc files can't hang startup, and say so when it trips.
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeSeconds));
		try {
			var result = await ProcessCapture.RunAsync(
				new ProcessCaptureRequest {
					FileName = shell,
					// `-i` is essential: vars usually live in the interactive rc (~/.zshrc), not the login profile (~/.zprofile).
					Arguments = ["-l", "-i", "-c", $"printf %s '{Begin}'; /usr/bin/env -0; printf %s '{End}'"],
				},
				deadline.Token).ConfigureAwait(false);
			if (result.StartFailure is { } failure) {
				_failure = $"Weavie could not read your shell environment: {failure.Message}";
				return null;
			}

			string? fenced = ExtractFenced(result.StdOut);
			_failure = fenced is null ? HijackedMessage(shell) : string.Empty;
			return fenced;
		} catch (OperationCanceledException) {
			_failure = $"Weavie could not read your shell environment: {shell} startup did not finish within {ProbeSeconds}s.";
			return null;
		}
	}

	/// <summary>Pulls the body between the fence markers, tolerating any rc-file stdout noise around it.</summary>
	internal static string? ExtractFenced(string stdout) {
		int start = stdout.IndexOf(Begin, StringComparison.Ordinal);
		int end = stdout.IndexOf(End, StringComparison.Ordinal);
		if (start < 0 || end <= start) {
			return null;
		}

		return stdout[(start + Begin.Length)..end];
	}

	/// <summary>Splits the NUL-delimited <c>env -0</c> body into name/value pairs, dropping malformed entries.</summary>
	internal static IReadOnlyList<KeyValuePair<string, string>> ParseEnv(string body) {
		var pairs = new List<KeyValuePair<string, string>>();
		foreach (string entry in body.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
			int eq = entry.IndexOf('=', StringComparison.Ordinal);
			if (eq > 0) {
				pairs.Add(new(entry[..eq], entry[(eq + 1)..]));
			}
		}

		return pairs;
	}

	/// <summary>
	/// The shell environment to apply, authoritative over the inherited one — the probe shell is our child, so it
	/// already carries every inherited var and only adds or overrides on top. Transient session noise aside.
	/// </summary>
	internal static IReadOnlyList<KeyValuePair<string, string>> ResolveImports(
		IReadOnlyList<KeyValuePair<string, string>> shellEnv) {
		var imports = new List<KeyValuePair<string, string>>();
		foreach (var pair in shellEnv) {
			if (!Skip.Contains(pair.Key)) {
				imports.Add(pair);
			}
		}

		return imports;
	}

	/// <summary><c>$SHELL</c> if it points at a real file, else the per-OS default login shell.</summary>
	internal static string LoginShell() {
		string? shell = Environment.GetEnvironmentVariable("SHELL");
		if (!string.IsNullOrEmpty(shell) && File.Exists(shell)) {
			return shell;
		}

		return OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
	}
}
