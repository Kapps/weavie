using System.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// Turns a <see cref="RemoteSession"/> into a supervised <c>Weavie.Headless</c> worker process. This is the
/// "worktree mode" spawn delegate of Option C: a plain OS process rooted at the session's worktree. Container
/// mode would be a sibling launcher with the same shape (build a <see cref="ProcessSupervisor"/>) — the rest
/// of the runner is unaware of which produced the worker. See docs/specs/remote-sessions.md.
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
	/// Builds (does not start) a supervisor that keeps a headless worker for <paramref name="session"/> alive.
	/// Policy is <see cref="RestartPolicy.OnFailure"/>: a crashed worker is relaunched with backoff, a clean
	/// exit (an intentional stop) is not. Call <see cref="ProcessSupervisor.Start"/> on the result.
	/// </summary>
	public ProcessSupervisor BuildSupervisor(RemoteSession session) {
		ArgumentNullException.ThrowIfNull(session);

		ProcessSupervisor supervisor = null!;
		Process? current = null;

		supervisor = new ProcessSupervisor(
			name: $"session:{session.Id}",
			start: _ => {
				var process = Spawn(session);
				current = process;
				// Capture this launch's process so a later restart's exit can't be misattributed.
				process.Exited += (_, _) => {
					int code = SafeExitCode(process);
					supervisor.NotifyExited(code);
				};
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

	private Process Spawn(RemoteSession session) {
		bool isDll = _options.HeadlessPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo {
			FileName = isDll ? "dotnet" : _options.HeadlessPath,
			WorkingDirectory = session.WorktreePath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		if (isDll) {
			info.ArgumentList.Add(_options.HeadlessPath);
		}

		info.ArgumentList.Add("--port");
		info.ArgumentList.Add(session.Port.ToString());
		info.ArgumentList.Add("--bind");
		info.ArgumentList.Add(_options.WorkerBind);
		info.ArgumentList.Add("--workspace");
		info.ArgumentList.Add(session.WorktreePath);
		info.ArgumentList.Add("--token");
		info.ArgumentList.Add(session.Token);

		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		string tag = $"[session:{session.Id}]";
		process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Console.WriteLine($"{tag} {e.Data}"); } };
		process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Console.WriteLine($"{tag} {e.Data}"); } };
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
