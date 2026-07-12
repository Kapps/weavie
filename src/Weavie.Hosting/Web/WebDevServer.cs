// Hot-reload dev server — Debug-only. Release loads the bundled web assets, so the whole type is compiled out
// of Release builds to stay dead-code-free under the zero-warning gate (an unused field / an uninstantiated
// class would otherwise fail the build).
#if DEBUG
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Weavie.Core.Processes;

namespace Weavie.Hosting.Web;

/// <summary>
/// Owns the Vite HMR dev server for Debug runs (shared by every desktop host). Each instance binds its own held
/// port and spawns Vite with <c>--strictPort</c>, so worktrees/instances run side by side without cross-talk; the
/// caller points its WebView at <see cref="Origin"/>. A <see cref="ProcessSupervisor"/>
/// (<see cref="RestartPolicy.Always"/>) relaunches a crash on the same port so the origin stays valid.
/// <see cref="StartAsync"/> returns <c>null</c> on failure, recording the reason in <see cref="LastFailure"/>.
/// </summary>
public sealed class WebDevServer : IDisposable {
	private const int RecentCap = 40;

	private readonly Action<string> _log;
	private readonly string? _webDevRoot;
	// Tail of the dev server's output, so a failure can show the developer *why* it didn't come up (e.g. the
	// broken-dependency stack trace) instead of the host silently serving a stale bundle.
	private readonly Queue<string> _recent = new();
	private readonly ProcessSupervisor _supervisor;
	private Process? _process;
	private int _port;
	private string _probeUrl = string.Empty;

	/// <summary>The origin Vite serves on once <see cref="StartAsync"/> has picked a port (empty before then).
	/// Uses the <c>localhost</c> host (not 127.0.0.1) so the browser's Origin header equals what the host hands
	/// the LSP bridge as the allowed origin — the bridge compares the two byte-for-byte.</summary>
	public string Origin { get; private set; } = string.Empty;

	/// <summary>Set whenever <see cref="StartAsync"/> returns <c>null</c>: the failure reason plus the tail of
	/// captured Vite output. Cleared at the start of each <see cref="StartAsync"/>.</summary>
	public DevServerFailureInfo? LastFailure { get; private set; }

	/// <param name="log">Sink for the dev server's output lines.</param>
	/// <param name="webDevRoot">The Vite source dir (from <see cref="DevWebRoot"/>), or <c>null</c> when it couldn't be resolved.</param>
	public WebDevServer(Action<string> log, string? webDevRoot) {
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
		_webDevRoot = webDevRoot;
		// Vite is a permanent fixture of a Debug run, so policy Always: any exit (crash or otherwise) relaunches
		// with backoff until the crash-loop breaker trips. Lifecycle logging is folded into the captured output.
		_supervisor = new ProcessSupervisor(
			name: "vite",
			start: StartProcess,
			stop: StopProcess,
			options: new SupervisionOptions { Policy = RestartPolicy.Always },
			log: entry => Emit(entry.Message),
			clock: null);
	}

	/// <summary>
	/// Picks this instance's port (once), starts the supervised Vite process, and polls until it serves. Returns
	/// <see cref="Origin"/> on success, or <c>null</c> (with <see cref="LastFailure"/> populated) if the source dir
	/// is missing or it never comes up. Safe to call again (Retry / revive): a running server is left as-is.
	/// </summary>
	public async Task<string?> StartAsync() {
		LastFailure = null;
		if (string.IsNullOrEmpty(_webDevRoot)) {
			SetFailure("web dev dir not found (WeavieWebDevDir metadata absent or missing on disk)");
			return null;
		}

		// Pick a free port once and hold it: every restart reuses it (so a crash doesn't invalidate the WebView's
		// origin), and a per-instance port lets multiple worktrees run at once without cross-talk.
		if (_port == 0) {
			_port = PickFreePort();
			Origin = $"http://localhost:{_port}";
			_probeUrl = Origin + "/";
		}

		// No-op if the supervisor is already Running/BackingOff (e.g. a revive after a transient crash); it
		// relaunches Vite on the same port if it had given up (Failed → Start).
		_supervisor.Start();

		if (await WaitUntilReadyAsync(TimeSpan.FromSeconds(40)).ConfigureAwait(false)) {
			return Origin;
		}

		// Bring-up failed: halt background restarts so the error page's Retry (which calls StartAsync again) gets
		// a clean start, and a broken Vite isn't left hot-looping behind the rendered error page.
		_supervisor.Stop();
		return null;
	}

	/// <summary>Spawns a fresh Vite on this instance's port and wires its exit back to the supervisor. The
	/// supervisor's <c>start</c> delegate; an exception here is treated by the supervisor as a failed launch.</summary>
	private void StartProcess(SupervisedLaunch launch) {
		var process = new Process {
			StartInfo = new ProcessStartInfo {
				// Spawn Vite directly, not via a pnpm/npm shim: the shim exits once Vite is up, severing the
				// parent→child chain Kill(entireProcessTree) walks, so node(vite)/esbuild would orphan. --strictPort
				// fails loud if the port was grabbed since we released it, rather than wandering.
				FileName = "node",
				Arguments = $"node_modules/vite/bin/vite.js --port {_port} --strictPort",
				WorkingDirectory = _webDevRoot!,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			},
			EnableRaisingEvents = true,
		};
		process.OutputDataReceived += (_, e) => {
			if (e.Data is not null) {
				Emit(e.Data);
			}
		};
		process.ErrorDataReceived += (_, e) => {
			if (e.Data is not null) {
				Emit(e.Data);
			}
		};
		// Report through this launch's handle so a later restart's exit can't be misattributed (mirrors HeadlessLauncher).
		process.Exited += (_, _) => launch.NotifyExited(SafeExitCode(process));
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		_process = process;
	}

	/// <summary>Kills the current Vite tree. The supervisor's <c>stop</c> delegate; a safe no-op when idle.</summary>
	private void StopProcess() {
		var process = _process;
		if (process is null) {
			return;
		}

		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
				process.WaitForExit(3000);
			}
		} catch {
			// Best-effort teardown — the host's kill-on-close Job Object reaps any survivor when the host exits.
		} finally {
			process.Dispose();
			_process = null;
		}
	}

	private async Task<bool> WaitUntilReadyAsync(TimeSpan timeout) {
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < timeout) {
			if (await ProbeAsync(http).ConfigureAwait(false)) {
				return true;
			}

			// The supervisor relaunches a crashing Vite with backoff; if it has given up (crash-loop breaker
			// tripped), there's nothing left to wait for — fail now with the captured output explaining why.
			if (_supervisor.State == SupervisorState.Failed) {
				SetFailure("Vite crashed repeatedly during startup; the supervisor gave up restarting it");
				return false;
			}

			await Task.Delay(250).ConfigureAwait(false);
		}

		SetFailure($"dev server did not respond at {_probeUrl} within {timeout.TotalSeconds:N0}s");
		return false;
	}

	private async Task<bool> ProbeAsync(HttpClient http) {
		try {
			using var resp = await http.GetAsync(_probeUrl).ConfigureAwait(false);
			return resp.IsSuccessStatusCode;
		} catch {
			return false;
		}
	}

	/// <summary>Reserves a free loopback TCP port from the OS and releases it for Vite to bind. The OS won't hand
	/// out a port already in use, so concurrent instances never collide; the brief release→bind window is covered
	/// by Vite's <c>--strictPort</c> (it fails loud rather than silently moving to another port).</summary>
	private static int PickFreePort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}

	private static int SafeExitCode(Process process) {
		try {
			return process.HasExited ? process.ExitCode : -1;
		} catch {
			return -1;
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

	/// <summary>Stops and disposes the supervised Vite process (graceful teardown on a clean host exit).</summary>
	public void Dispose() => _supervisor.Dispose();
}

/// <summary>Why the Vite dev server failed to come up, plus the tail of its captured output — enough for the
/// host to show the developer the actual cause instead of silently degrading to a stale bundle.</summary>
public sealed record DevServerFailureInfo(string Summary, IReadOnlyList<string> OutputTail);
#endif
