using System.Net;
using System.Net.Sockets;
using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// The runner's manager: owns the (single, multi-session) <see cref="WorkspaceBackend"/> worker for the
/// configured workspace, starting it on demand and supervising it. Worktree sessions are created inside the
/// worker by the shared <c>HostCore</c>, so the manager only provisions + auths the backend — it does not
/// manage individual sessions. See docs/specs/remote-sessions.md.
/// </summary>
public sealed class BackendManager : IAsyncDisposable {
	private readonly RunnerOptions _options;
	private readonly HeadlessLauncher _launcher;
	private readonly object _gate = new();
	private WorkspaceBackend? _backend;

	/// <summary>Creates a manager that provisions workers per <paramref name="options"/>.</summary>
	public BackendManager(RunnerOptions options, HeadlessLauncher launcher) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(launcher);
		_options = options;
		_launcher = launcher;
	}

	/// <summary>The current backend, or <c>null</c> before the first <see cref="Ensure"/>.</summary>
	public WorkspaceBackend? Current {
		get {
			lock (_gate) {
				return _backend;
			}
		}
	}

	/// <summary>
	/// Returns the workspace backend, starting a fresh worker when none is running (or the previous one tripped
	/// its supervisor breaker). The returned worker may still be <c>starting</c>; the client connects to its URL
	/// and the bridge re-attaches once it is up.
	/// </summary>
	public WorkspaceBackend Ensure() {
		lock (_gate) {
			if (_backend is { Supervisor.State: not SupervisorState.Failed }) {
				return _backend;
			}

			_backend?.Supervisor?.Dispose();
			var backend = new WorkspaceBackend {
				WorkspaceRoot = _options.WorkspaceRoot,
				Port = AllocatePort(),
				Token = RunnerOptions.NewToken(),
			};
			backend.Supervisor = _launcher.BuildSupervisor(backend);
			_backend = backend;
			backend.Supervisor.Start();
			return backend;
		}
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		lock (_gate) {
			_backend?.Supervisor?.Dispose();
			_backend = null;
		}

		return ValueTask.CompletedTask;
	}

	/// <summary>Grabs a free TCP port by binding to port 0 and releasing it. Inherently racy, fine here.</summary>
	private static int AllocatePort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}
}
