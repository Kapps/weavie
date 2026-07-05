using System.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// Turns a <see cref="WorkspaceBackend"/> into a supervised <c>Weavie.Headless</c> worker process rooted at the
/// workspace root (worktree mode): a plain OS process whose shared HostCore creates per-session worktrees on
/// demand. See docs/specs/remote-sessions.md.
/// </summary>
public sealed class HeadlessLauncher {
	private readonly Func<string> _workerPath;
	private readonly string _workerBind;
	private readonly Action<SupervisorLogEntry>? _log;

	/// <summary>
	/// Creates a launcher that spawns the headless build <paramref name="workerPath"/> resolves — re-read on
	/// every spawn, so an updated <c>current</c> version takes effect on the next launch without touching the
	/// running worker — binding each worker to <paramref name="workerBind"/> (the <see cref="ITlsFront"/>'s
	/// worker interface — loopback when fronted).
	/// </summary>
	public HeadlessLauncher(Func<string> workerPath, string workerBind, Action<SupervisorLogEntry>? log) {
		ArgumentNullException.ThrowIfNull(workerPath);
		ArgumentNullException.ThrowIfNull(workerBind);
		_workerPath = workerPath;
		_workerBind = workerBind;
		_log = log;
	}

	/// <summary>
	/// Builds (does not start) a supervisor that keeps a headless worker for <paramref name="backend"/> alive
	/// under <see cref="RestartPolicy.OnFailure"/>: a crash relaunches with backoff, a clean exit does not.
	/// <paramref name="reallocatePort"/>, when not <c>null</c> (unpinned port), is called before a restart whose
	/// PREVIOUS attempt logged a bind conflict (<c>AllocatePort</c>'s bind-then-release probe is inherently
	/// racy) — so that specific restart doesn't retry the exact port that just failed to bind. Any other crash
	/// restarts on the same port: a worker that had actually bound and was serving tabs must come back on the
	/// same address, or every open tab's WebSocket reconnect (which retries a fixed URL, never re-resolves a
	/// new port) would be stranded pointing at a dead port forever.
	/// </summary>
	public ProcessSupervisor BuildSupervisor(WorkspaceBackend backend, Func<int>? reallocatePort) {
		ArgumentNullException.ThrowIfNull(backend);

		ProcessSupervisor supervisor = null!;
		Process? current = null;
		bool addressInUse = false;

		supervisor = new ProcessSupervisor(
			name: "backend",
			start: attempt => {
				if (attempt > 0 && reallocatePort is not null && addressInUse) {
					backend.Port = reallocatePort();
				}

				addressInUse = false;
				var process = Spawn(backend, line => {
					if (line.Contains("AddressInUseException", StringComparison.Ordinal)) {
						addressInUse = true;
					}
				});
				current = process;
				// Capture this launch's process so a later restart's exit can't be misattributed.
				process.Exited += (_, _) => supervisor.NotifyExited(SafeExitCode(process));
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
			},
			stop: () => {
				try {
					if (current is { HasExited: false }) {
						current.Kill(entireProcessTree: true);
					}
				} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
					// Already gone / unkillable; nothing to do.
				}
			},
			options: new SupervisionOptions { Policy = RestartPolicy.OnFailure },
			log: _log,
			clock: null);

		return supervisor;
	}

	// `onOutputLine` sees every stdout/stderr line (mirroring what's already printed to the console) so the
	// caller can watch for a specific failure signature — e.g. a bind conflict — without a second read of the
	// process output.
	private Process Spawn(WorkspaceBackend backend, Action<string> onOutputLine) {
		string workerPath = _workerPath();
		bool isDll = workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo {
			FileName = isDll ? "dotnet" : workerPath,
			WorkingDirectory = backend.WorkspaceRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		if (isDll) {
			info.ArgumentList.Add(workerPath);
		}

		// Workers are network-exposed: --remote requires the token (the worker refuses to start otherwise).
		info.ArgumentList.Add("--remote");
		info.ArgumentList.Add("--port");
		info.ArgumentList.Add(backend.Port.ToString());
		info.ArgumentList.Add("--bind");
		info.ArgumentList.Add(_workerBind);
		info.ArgumentList.Add("--workspace");
		info.ArgumentList.Add(backend.WorkspaceRoot);
		info.ArgumentList.Add("--token");
		info.ArgumentList.Add(backend.Token);

		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		process.OutputDataReceived += (_, e) => Report(e.Data);
		process.ErrorDataReceived += (_, e) => Report(e.Data);
		return process;

		void Report(string? data) {
			if (data is null) {
				return;
			}

			Console.WriteLine($"[backend] {data}");
			onOutputLine(data);
		}
	}

	private static int SafeExitCode(Process process) {
		try {
			return process.ExitCode;
		} catch (InvalidOperationException) {
			return -1;
		}
	}
}
