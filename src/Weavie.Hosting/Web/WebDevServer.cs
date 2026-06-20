// Hot-reload dev server — Debug-only. Release loads the bundled web assets, so the whole type is compiled out
// of Release builds to stay dead-code-free under the zero-warning gate (an unused field / an uninstantiated
// class would otherwise fail the build).
#if DEBUG
using System.Diagnostics;

namespace Weavie.Hosting.Web;

/// <summary>
/// Owns the Vite dev server for hot-module reload during Debug runs, shared by every desktop host. It reuses a
/// dev server already serving the port, or spawns Vite itself (so there's no second terminal to babysit), waits
/// for it to serve, and the caller points its WebView at <see cref="Origin"/>. A server this instance spawned is
/// killed on <see cref="Dispose"/>; a reused one is left alone. <see cref="StartAsync"/> returns <c>null</c> when
/// the source dir or the server is unavailable, and records the reason in <see cref="LastFailure"/> so the caller
/// can show the developer <em>why</em> rather than silently degrading to a stale bundle.
/// </summary>
public sealed class WebDevServer : IDisposable {
	// Must match `server.port` (strictPort) in src/web/vite.config.ts. Use the `localhost` host (not 127.0.0.1)
	// so the browser's Origin header equals what we hand the LSP bridge as the allowed origin — the bridge
	// compares the two byte-for-byte.
	/// <summary>The origin Vite serves on (strict port), shared by every host's dev-server wiring.</summary>
	public const string Origin = "http://localhost:5173";
	private const string ProbeUrl = Origin + "/";
	private const int RecentCap = 40;

	private readonly Action<string> _log;
	private readonly string? _webDevRoot;
	// Tail of the dev server's output, so a failure can show the developer *why* it didn't come up (e.g. the
	// broken-dependency stack trace) instead of the host silently serving a stale bundle.
	private readonly Queue<string> _recent = new();
	private Process? _process;

	/// <summary>Set whenever <see cref="StartAsync"/> returns <c>null</c>: the failure reason plus the tail of
	/// captured Vite output. Cleared at the start of each <see cref="StartAsync"/>.</summary>
	public DevServerFailureInfo? LastFailure { get; private set; }

	/// <param name="log">Sink for the dev server's output lines.</param>
	/// <param name="webDevRoot">The Vite source dir (from <see cref="DevWebRoot"/>), or <c>null</c> when it couldn't be resolved.</param>
	public WebDevServer(Action<string> log, string? webDevRoot) {
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
		_webDevRoot = webDevRoot;
	}

	/// <summary>
	/// Reuses or spawns the Vite dev server and polls until it serves. Returns <see cref="Origin"/> on success,
	/// or <c>null</c> (with <see cref="LastFailure"/> populated) if the source dir is missing or it never comes up.
	/// </summary>
	public async Task<string?> StartAsync() {
		LastFailure = null;
		if (string.IsNullOrEmpty(_webDevRoot)) {
			SetFailure("web dev dir not found (WeavieWebDevDir metadata absent or missing on disk)");
			return null;
		}

		// Reuse a dev server already serving the port instead of spawning a second one. Vite uses strictPort, so
		// a second `pnpm run dev` would only collide ("Port 5173 already in use") and exit, leaving the WebView
		// bound to a server this instance neither owns nor reaps — and when that foreign server later dies the
		// page breaks (ERR_CONNECTION_REFUSED) with no fallback. A hand-started server, or one from a not-yet-
		// reaped prior run, is reused as-is; _process stays null so Dispose never kills a server we didn't start.
		using (var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }) {
			if (await ProbeAsync(probeHttp).ConfigureAwait(false)) {
				Emit($"reusing dev server already serving at {Origin}");
				return Origin;
			}
		}

		try {
			_process = new Process {
				StartInfo = new ProcessStartInfo {
					// Spawn Vite directly rather than via a pnpm shim. The shim exits as soon as Vite is up,
					// severing the parent→child chain Kill(entireProcessTree) walks on teardown — so node(vite)/
					// esbuild would orphan (holding port 5173, keeping the run session alive). Launching Vite's
					// entry with node makes _process the actual Vite process, so its lone child (esbuild) is reaped
					// reliably. (`dev` is just `vite`.)
					FileName = "node",
					Arguments = "node_modules/vite/bin/vite.js",
					WorkingDirectory = _webDevRoot,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				},
				EnableRaisingEvents = true,
			};
			_process.OutputDataReceived += (_, e) => {
				if (e.Data is not null) {
					Emit(e.Data);
				}
			};
			_process.ErrorDataReceived += (_, e) => {
				if (e.Data is not null) {
					Emit(e.Data);
				}
			};
			_process.Start();
			_process.BeginOutputReadLine();
			_process.BeginErrorReadLine();
		} catch (Exception ex) {
			SetFailure($"failed to start Vite (node): {ex.Message}");
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
			// If our process died and nothing else is already serving the port, stop waiting. (Race guard: if
			// another server grabbed the port between our up-front probe and this spawn, our spawn exits fast on
			// the strict-port collision but the probe still succeeds — reuse it.)
			if (_process is { HasExited: true }) {
				bool up = await ProbeAsync(http).ConfigureAwait(false);
				if (!up) {
					SetFailure($"Vite exited (code {_process.ExitCode}) before the server was ready");
				}

				return up;
			}

			await Task.Delay(250).ConfigureAwait(false);
		}

		SetFailure($"dev server did not respond at {ProbeUrl} within {timeout.TotalSeconds:N0}s");
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

	/// <summary>Logs a line via the host's writer and keeps the last <see cref="RecentCap"/> lines for failure reports.</summary>
	private void Emit(string line) {
		_recent.Enqueue(line);
		while (_recent.Count > RecentCap) {
			_recent.Dequeue();
		}

		_log(line);
	}

	/// <summary>Records the failure reason (and snapshots the captured output tail) and emits it to the log.</summary>
	private void SetFailure(string reason) {
		Emit(reason);
		LastFailure = new DevServerFailureInfo(reason, _recent.ToArray());
	}

	/// <summary>Kills the Vite process this instance spawned (a reused server is left running).</summary>
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

/// <summary>Why the Vite dev server failed to come up, plus the tail of its captured output — enough for the
/// host to show the developer the actual cause instead of silently degrading to a stale bundle.</summary>
public sealed record DevServerFailureInfo(string Summary, IReadOnlyList<string> OutputTail);
#endif
