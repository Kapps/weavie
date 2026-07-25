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
	/// </summary>
	public ProcessSupervisor BuildSupervisor(WorkspaceBackend backend) {
		ArgumentNullException.ThrowIfNull(backend);

		ProcessSupervisor supervisor = null!;
		Process? current = null;
		// Flipped from the child's stdout/stderr-reading thread (BackendPortConflicted below), read from the
		// restart's thread — both cross a ThreadPool boundary, so Interlocked rather than a bare bool.
		int portConflicted = 0;

		supervisor = new ProcessSupervisor(
			name: "backend",
			start: launch => {
				// The previous attempt's port lost a race to another process's bind (AllocatePort is inherently
				// racy — see its doc comment) — pick a fresh one before retrying the doomed one forever, unless
				// it's pinned (secured/fronted modes need a stable port across restarts).
				if (Interlocked.Exchange(ref portConflicted, 0) == 1 && !backend.PortIsPinned) {
					backend.Port = BackendManager.AllocatePort();
				}

				var process = Spawn(backend, () => Interlocked.Exchange(ref portConflicted, 1));
				current = process;
				// Report through this launch's handle so a later restart's exit can't be misattributed.
				process.Exited += (_, _) => launch.NotifyExited(SafeExitCode(process));
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

	private Process Spawn(WorkspaceBackend backend, Action onPortConflict) {
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
		process.OutputDataReceived += (_, e) => { if (e.Data is not null) { LogBackendLine(e.Data, onPortConflict); } };
		process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { LogBackendLine(e.Data, onPortConflict); } };
		return process;
	}

	private static void LogBackendLine(string line, Action onPortConflict) {
		Console.WriteLine($"[backend] {line}");
		if (IsPortConflictLine(line)) {
			onPortConflict();
		}
	}

	// .NET renders both AddressInUseException and a raw EADDRINUSE SocketException with this exact phrase —
	// stable across the runtime versions this targets, and specific enough not to false-positive on other crashes.
	internal static bool IsPortConflictLine(string line) =>
		line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase);

	private static int SafeExitCode(Process process) {
		try {
			return process.ExitCode;
		} catch (InvalidOperationException) {
			return -1;
		}
	}
}
