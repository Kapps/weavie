using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

		supervisor = new ProcessSupervisor(
			name: "backend",
			start: launch => {
				if (ShouldRepickPort(backend, _workerBind)) {
					int taken = backend.Port;
					backend.Port = BackendManager.AllocatePort();
					Log($"port {taken} is held by another listener; moving the worker to {backend.Port}");
				}

				var process = Spawn(backend);
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
			options: new SupervisionOptions {
				Policy = RestartPolicy.OnFailure,
				MaxConsecutiveFailures = 3,
				RequireExplicitHealth = true,
			},
			log: _log,
			clock: null);

		return supervisor;
	}

	private void Log(string message) =>
		_log?.Invoke(new SupervisorLogEntry("backend", SupervisorLogLevel.Warning, message));

	/// <summary>
	/// Whether the worker's next launch needs a different port: only when something else is already listening
	/// on it, which the worker's own bind would then fail. A pinned port is never repicked — secured/fronted
	/// modes map it — so those launches fail loudly on the conflict instead.
	/// </summary>
	internal static bool ShouldRepickPort(WorkspaceBackend backend, string workerBind) {
		ArgumentNullException.ThrowIfNull(backend);
		return !backend.PortIsPinned && !PortIsFree(workerBind, backend.Port);
	}

	/// <summary>
	/// Whether <paramref name="port"/> can still be bound on <paramref name="workerBind"/>. Asking the OS is
	/// what makes the repick exact: a worker that died for its own reasons finds its port free and keeps it.
	/// </summary>
	internal static bool PortIsFree(string workerBind, int port) {
		ArgumentNullException.ThrowIfNull(workerBind);
		// --worker-bind takes "localhost" alongside a literal address; anything else non-literal the options
		// layer has already refused, so it throws here rather than being quietly reinterpreted.
		var address = workerBind == "localhost" ? IPAddress.Loopback : IPAddress.Parse(workerBind);
		var listener = new TcpListener(address, port);
		try {
			listener.Start();
			return true;
		} catch (SocketException) {
			return false;
		} finally {
			listener.Stop();
		}
	}

	private Process Spawn(WorkspaceBackend backend) {
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
		info.ArgumentList.Add("--spawn-contract");
		info.ArgumentList.Add(RunnerIdentity.SpawnContract.ToString());

		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		static void Echo(string? line) {
			if (line is not null) {
				Console.WriteLine($"[backend] {line}");
			}
		}

		process.OutputDataReceived += (_, e) => Echo(e.Data);
		process.ErrorDataReceived += (_, e) => Echo(e.Data);
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
