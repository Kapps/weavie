// Hot-reload dev server — Debug-only. Release loads the bundled wwwroot, so the whole type is
// compiled out of Release builds to stay dead-code-free under the zero-warning gate (an unused
// field / an uninstantiated internal class would otherwise fail the build).
#if DEBUG
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;

namespace Weavie.Win.Hosting;

/// <summary>
/// Owns the Vite dev server for hot-module reload during Debug runs. The host spawns
/// <c>npm run dev</c> itself (so there's no second terminal to babysit), waits for it to start
/// serving, and the caller points the WebView at <see cref="Origin"/>. The whole process tree is
/// killed on <see cref="Dispose"/>. <see cref="StartAsync"/> returns <c>null</c> when the web
/// source dir or the server is unavailable, so the caller can fall back to the bundled wwwroot.
/// </summary>
internal sealed class WebDevServer : IDisposable {
	// Must match `server.port` (strictPort) in src/web/vite.config.ts. Use the `localhost` host (not
	// 127.0.0.1) so the browser's Origin header equals what we hand the LSP bridge as the allowed
	// origin — the bridge compares the two byte-for-byte.
	public const string Origin = "http://localhost:5173";
	private const string ProbeUrl = Origin + "/";

	private readonly Action<string> _log;
	private Process? _process;

	public WebDevServer(Action<string> log) {
		_log = log;
	}

	/// <summary>
	/// Resolves the web source dir (injected as assembly metadata at build time), spawns the Vite
	/// dev server there, and polls until it serves. Returns <see cref="Origin"/> on success, or
	/// <c>null</c> if the directory is missing or the server never comes up.
	/// </summary>
	public async Task<string?> StartAsync() {
		var devDir = ResolveWebDevDir();
		if (devDir is null) {
			_log("web dev dir not found (WeavieWebDevDir metadata absent or missing on disk)");
			return null;
		}

		try {
			_process = new Process {
				StartInfo = new ProcessStartInfo {
					// npm is a .cmd shim on Windows — go through cmd.exe so it resolves on PATH.
					FileName = "cmd.exe",
					Arguments = "/c npm run dev",
					WorkingDirectory = devDir,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				},
				EnableRaisingEvents = true,
			};
			_process.OutputDataReceived += (_, e) => {
				if (e.Data is not null) {
					_log(e.Data);
				}
			};
			_process.ErrorDataReceived += (_, e) => {
				if (e.Data is not null) {
					_log(e.Data);
				}
			};
			_process.Start();
			_process.BeginOutputReadLine();
			_process.BeginErrorReadLine();
		} catch (Exception ex) {
			_log($"failed to start `npm run dev`: {ex.Message}");
			return null;
		}

		return await WaitUntilReadyAsync(TimeSpan.FromSeconds(40)).ConfigureAwait(false) ? Origin : null;
	}

	private async Task<bool> WaitUntilReadyAsync(TimeSpan timeout) {
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < timeout) {
			if (await ProbeAsync(http).ConfigureAwait(false)) {
				return true;
			}
			// If our process died and nothing else is already serving the port, stop waiting. (A port
			// taken by a prior dev server makes our spawn exit fast but the probe still succeeds — we
			// happily reuse that server.)
			if (_process is { HasExited: true }) {
				var up = await ProbeAsync(http).ConfigureAwait(false);
				if (!up) {
					_log($"`npm run dev` exited (code {_process.ExitCode}) before the server was ready");
				}
				return up;
			}
			await Task.Delay(250).ConfigureAwait(false);
		}
		_log($"dev server did not respond at {ProbeUrl} within {timeout.TotalSeconds:N0}s");
		return false;
	}

	private static async Task<bool> ProbeAsync(HttpClient http) {
		try {
			using var resp = await http.GetAsync(ProbeUrl).ConfigureAwait(false);
			return resp.IsSuccessStatusCode;
		} catch {
			return false;
		}
	}

	/// <summary>Reads the web source dir from the <c>WeavieWebDevDir</c> assembly metadata that the
	/// Debug build injects (see Weavie.Win.csproj), normalized to a full path that exists.</summary>
	private static string? ResolveWebDevDir() {
		var raw = Assembly.GetExecutingAssembly()
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => string.Equals(a.Key, "WeavieWebDevDir", StringComparison.Ordinal))
			?.Value;
		if (string.IsNullOrEmpty(raw)) {
			return null;
		}
		var full = Path.GetFullPath(raw);
		return Directory.Exists(full) ? full : null;
	}

	public void Dispose() {
		if (_process is null) {
			return;
		}
		try {
			if (!_process.HasExited) {
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit(3000);
			}
		} catch {
			// Best-effort teardown — the OS reaps the tree when the host exits regardless.
		} finally {
			_process.Dispose();
			_process = null;
		}
	}
}
#endif
