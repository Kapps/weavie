using System.Diagnostics;

namespace Weavie.Hosting;

/// <summary>
/// One-time import of the user's login-shell <c>PATH</c> into this process. A macOS <c>.app</c> launched from
/// Finder/<c>open</c> inherits launchd's minimal <c>PATH</c> (no <c>~/.dotnet/tools</c>, Homebrew, …), so the
/// children Weavie spawns directly — the LSP servers (e.g. <c>csharp-ls</c>), <c>git</c> — can't be found even
/// though a terminal launch resolves them. Running the interactive login shell once and merging its <c>PATH</c>
/// fixes every later spawn. (PTY children sidestep this by exec-ing through a login shell themselves.)
/// </summary>
public static class LoginShellPath {
	private const string Begin = "__WEAVIE_PATH_BEGIN__";
	private const string End = "__WEAVIE_PATH_END__";

	private static bool _imported;

	/// <summary>
	/// On the first call from a macOS app-bundle launch, imports the login-shell <c>PATH</c> into this process so
	/// later spawns resolve executables as a terminal launch would. A no-op otherwise — terminal/dev launches
	/// already inherit the full <c>PATH</c> — and on every later call.
	/// </summary>
	/// <param name="log">Sink for a one-line note of what was imported, or why the probe was skipped.</param>
	public static async Task ImportOnceAsync(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		if (_imported) {
			return;
		}

		_imported = true;
		if (!IsBundledMacApp()) {
			return;
		}

		string? shellPath = await ReadLoginShellPathAsync(log).ConfigureAwait(false);
		if (string.IsNullOrEmpty(shellPath)) {
			return;
		}

		string merged = Merge(shellPath, Environment.GetEnvironmentVariable("PATH"));
		Environment.SetEnvironmentVariable("PATH", merged);
		log($"imported login-shell PATH ({merged.Split(Path.PathSeparator).Length} entries)");
	}

	// The published Mac binary runs at <Weavie.Mac.app>/Contents/MacOS/Weavie.Mac; a terminal/`dotnet run` does not.
	private static bool IsBundledMacApp() =>
		OperatingSystem.IsMacOS()
		&& Environment.ProcessPath?.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) == true;

	// Runs `$SHELL -l -i -c 'printf …'` and returns the PATH fenced between markers, or null on any failure. `-i`
	// is essential: PATH entries usually live in the interactive rc (~/.zshrc), not the login profile (~/.zprofile).
	private static async Task<string?> ReadLoginShellPathAsync(Action<string> log) {
		var psi = new ProcessStartInfo {
			FileName = LoginShell(),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-l");
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add($"printf '%s' \"{Begin}${{PATH}}{End}\"");

		try {
			using var process = Process.Start(psi);
			if (process is null) {
				return null;
			}

			// Drain both pipes so a chatty rc file can't deadlock the child on a full buffer, and bound the whole
			// probe so a misconfigured shell can't hang startup — the failure stays loud through the log.
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
			_ = process.StandardError.ReadToEndAsync(cts.Token);
			await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
			return ExtractFenced(await stdout.ConfigureAwait(false));
		} catch (OperationCanceledException) {
			log("login-shell PATH probe timed out; keeping inherited PATH");
			return null;
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
			log($"login-shell PATH probe failed: {ex.Message}; keeping inherited PATH");
			return null;
		}
	}

	/// <summary>Pulls the value between the fence markers, tolerating any rc-file stdout noise around it.</summary>
	internal static string? ExtractFenced(string stdout) {
		int start = stdout.IndexOf(Begin, StringComparison.Ordinal);
		int end = stdout.IndexOf(End, StringComparison.Ordinal);
		if (start < 0 || end <= start) {
			return null;
		}

		return stdout[(start + Begin.Length)..end];
	}

	/// <summary>Unions the shell <c>PATH</c> ahead of the inherited one, de-duplicated — only ever adds reachability.</summary>
	internal static string Merge(string shellPath, string? currentPath) {
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var ordered = new List<string>();
		foreach (string entry in $"{shellPath}{Path.PathSeparator}{currentPath}".Split(
			Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (seen.Add(entry)) {
				ordered.Add(entry);
			}
		}

		return string.Join(Path.PathSeparator, ordered);
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
