using System.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// Turns a <see cref="WorkspaceBackend"/> into a supervised <c>Weavie.Headless</c> worker process rooted at
/// the workspace root. This is the "worktree mode" of Option C: a plain OS process; the shared HostCore inside
/// it creates per-session worktrees on demand. Container mode would be a sibling launcher of the same shape
/// (build a <see cref="ProcessSupervisor"/> over a container) — the rest of the runner is unaware which
/// produced the worker. See docs/specs/remote-sessions.md.
/// </summary>
public sealed class HeadlessLauncher {
	private readonly RunnerOptions _options;
	private readonly Action<SupervisorLogEntry>? _log;

	/// <summary>Creates a launcher that spawns the headless build named by <paramref name="options"/>.</summary>
	public HeadlessLauncher(RunnerOptions options, Action<SupervisorLogEntry>? log) {
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
		_log = log;
	}

	/// <summary>
	/// Builds (does not start) a supervisor that keeps a headless worker for <paramref name="backend"/> alive.
	/// Policy is <see cref="RestartPolicy.OnFailure"/>: a crashed worker is relaunched with backoff, a clean
	/// exit (an intentional stop) is not. Call <see cref="ProcessSupervisor.Start"/> on the result.
	/// </summary>
	public ProcessSupervisor BuildSupervisor(WorkspaceBackend backend) {
		ArgumentNullException.ThrowIfNull(backend);

		ProcessSupervisor supervisor = null!;
		Process? current = null;

		supervisor = new ProcessSupervisor(
			name: "backend",
			start: _ => {
				var process = Spawn(backend);
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

	private Process Spawn(WorkspaceBackend backend) {
		bool isDll = _options.HeadlessPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo {
			FileName = isDll ? "dotnet" : _options.HeadlessPath,
			WorkingDirectory = backend.WorkspaceRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		if (isDll) {
			info.ArgumentList.Add(_options.HeadlessPath);
		}

		info.ArgumentList.Add("--port");
		info.ArgumentList.Add(backend.Port.ToString());
		info.ArgumentList.Add("--bind");
		info.ArgumentList.Add(_options.WorkerBind);
		info.ArgumentList.Add("--workspace");
		info.ArgumentList.Add(backend.WorkspaceRoot);
		info.ArgumentList.Add("--token");
		info.ArgumentList.Add(backend.Token);

		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Console.WriteLine($"[backend] {e.Data}"); } };
		process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Console.WriteLine($"[backend] {e.Data}"); } };
		return process;
	}

	private static int SafeExitCode(Process process) {
		try {
			return process.ExitCode;
		} catch (InvalidOperationException) {
			return -1;
		}
	}
}
