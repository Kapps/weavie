// Hot-reload dev server — Debug-only. Release loads the bundled wwwroot, so the whole type is
// compiled out of Release builds to stay dead-code-free under the zero-warning gate (an unused
// field / an uninstantiated internal class would otherwise fail the build).
#if DEBUG
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;

namespace Weavie.Win.Hosting;

/// <summary>
/// Owns the Vite dev server for hot-module reload during Debug runs. The host reuses a dev server
/// already serving the port, or otherwise spawns Vite itself (so there's no second
/// terminal to babysit), waits for it to start serving, and the caller points the WebView at
/// <see cref="Origin"/>. A server this instance spawned is killed on <see cref="Dispose"/>; a reused
/// one is left alone. <see cref="StartAsync"/> returns <c>null</c> when the web source dir or the
/// server is unavailable, so the caller can fall back to the bundled wwwroot.
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
		string? devDir = ResolveWebDevDir();
		if (devDir is null) {
			_log("web dev dir not found (WeavieWebDevDir metadata absent or missing on disk)");
			return null;
		}

		// Reuse a dev server already serving the port instead of spawning a second one. Vite uses
		// strictPort, so a second `npm run dev` would only collide ("Port 5173 already in use") and
		// exit, leaving the WebView bound to a server this instance neither owns nor reaps — and when
		// that foreign server later dies the page breaks (ERR_CONNECTION_REFUSED) with no fallback.
		// A hand-started server, or one from a not-yet-reaped prior run, is reused as-is; _process
		// stays null so Dispose never tries to kill a server we didn't start.
		using (var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }) {
			if (await ProbeAsync(probeHttp).ConfigureAwait(false)) {
				_log($"reusing dev server already serving at {Origin}");
				return Origin;
			}
		}

		try {
			_process = new Process {
				StartInfo = new ProcessStartInfo {
					// Spawn Vite directly rather than via `cmd /c npm run dev`. The cmd/npm shims exit as
					// soon as Vite is up, severing the parent→child chain Kill(entireProcessTree) walks on
					// teardown — so node(vite)/esbuild would orphan (holding port 5173, keeping the run
					// session alive). Launching Vite's entry with node makes _process the actual Vite
					// process, so its lone child (esbuild) is reaped reliably. (`dev` is just `vite`.)
					FileName = "node",
					Arguments = "node_modules/vite/bin/vite.js",
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
			_log($"failed to start Vite (node): {ex.Message}");
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
			// If our process died and nothing else is already serving the port, stop waiting. (Race
			// guard: if another server grabbed the port between our up-front probe and this spawn, our
			// spawn exits fast on the strict-port collision but the probe still succeeds — reuse it.)
			if (_process is { HasExited: true }) {
				bool up = await ProbeAsync(http).ConfigureAwait(false);
				if (!up) {
					_log($"Vite exited (code {_process.ExitCode}) before the server was ready");
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
		string? raw = Assembly.GetExecutingAssembly()
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => string.Equals(a.Key, "WeavieWebDevDir", StringComparison.Ordinal))
			?.Value;
		if (string.IsNullOrEmpty(raw)) {
			return null;
		}
		string full = Path.GetFullPath(raw);
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
