using System.Diagnostics;
using System.Text;

namespace Weavie.Hosting;

/// <summary>
/// Imports the user's login-shell environment into this process. A GUI launch (a macOS <c>.app</c> from Finder,
/// a Linux desktop entry) inherits a minimal environment, so children Weavie spawns directly (LSP servers,
/// <c>git</c>) would otherwise miss <c>PATH</c> entries, <c>DOTNET_ROOT</c>, and the like a terminal launch has.
/// </summary>
public static class LoginShellEnvironment {
	private const string Begin = "__WEAVIE_ENV_BEGIN__";
	private const string End = "__WEAVIE_ENV_END__";

	// Transient session noise describing the probe subshell, not config worth propagating to children.
	private static readonly HashSet<string> Skip = new(StringComparer.Ordinal) { "_", "SHLVL", "PWD", "OLDPWD" };

	private static bool _imported;

	/// <summary>
	/// Imports the login-shell environment on the first call (macOS/Linux); a no-op on Windows and on later calls.
	/// The caller restricts this to the GUI hosts that may have a truncated environment.
	/// </summary>
	/// <param name="log">Sink for a one-line note of what was imported, or why the probe was skipped.</param>
	public static async Task ImportOnceAsync(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		if (_imported) {
			return;
		}

		_imported = true;
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) {
			return;
		}

		string? fenced = await ReadLoginShellEnvAsync(log).ConfigureAwait(false);
		if (string.IsNullOrEmpty(fenced)) {
			return;
		}

		var imports = ResolveImports(ParseEnv(fenced));
		foreach (var (name, value) in imports) {
			Environment.SetEnvironmentVariable(name, value);
		}

		log($"imported login-shell environment ({imports.Count} vars)");
	}

	// `-i` is essential: vars usually live in the interactive rc (~/.zshrc), not the login profile (~/.zprofile).
	private static async Task<string?> ReadLoginShellEnvAsync(Action<string> log) {
		var psi = new ProcessStartInfo {
			FileName = LoginShell(),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
		};
		psi.ArgumentList.Add("-l");
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add($"printf %s '{Begin}'; /usr/bin/env -0; printf %s '{End}'");

		try {
			using var process = Process.Start(psi);
			if (process is null) {
				return null;
			}

			// Drain both pipes so a chatty rc file can't deadlock the child, and bound the probe so a bad shell can't hang startup.
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
			_ = process.StandardError.ReadToEndAsync(cts.Token);
			await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
			return ExtractFenced(await stdout.ConfigureAwait(false));
		} catch (OperationCanceledException) {
			log("login-shell environment probe timed out; keeping inherited environment");
			return null;
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
			log($"login-shell environment probe failed: {ex.Message}; keeping inherited environment");
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
