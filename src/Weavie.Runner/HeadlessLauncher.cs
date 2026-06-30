using System.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// Turns a <see cref="WorkspaceBackend"/> into a supervised <c>Weavie.Headless</c> worker process rooted at the
/// workspace root (worktree mode): a plain OS process whose shared HostCore creates per-session worktrees on
/// demand. See docs/specs/remote-sessions.md.
/// </summary>
public sealed class HeadlessLauncher {
	private readonly RunnerOptions _options;
	private readonly string _workerBind;
	private readonly Action<SupervisorLogEntry>? _log;

	/// <summary>
	/// Creates a launcher that spawns the headless build named by <paramref name="options"/>, binding each worker
	/// to <paramref name="workerBind"/> (the <see cref="ITlsFront"/>'s worker interface — loopback when fronted).
	/// </summary>
	public HeadlessLauncher(RunnerOptions options, string workerBind, Action<SupervisorLogEntry>? log) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(workerBind);
		_options = options;
		_workerBind = workerBind;
		_log = log;
	}

	/// <summary>
	/// Builds (does not start) a supervisor that keeps a headless worker for <paramref name="backend"/> alive
	/// under <see cref="RestartPolicy.OnFailure"/>: a crash relaunches with backoff, a clean exit does not.
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
