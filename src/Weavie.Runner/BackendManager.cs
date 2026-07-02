using System.Net;
using System.Net.Sockets;
using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// Owns and supervises the single multi-session <see cref="WorkspaceBackend"/> worker on demand. Worktree
/// sessions live inside the worker's shared <c>HostCore</c>, so the manager provisions + auths the backend,
/// not individual sessions. See docs/specs/remote-sessions.md.
/// </summary>
public sealed partial class BackendManager : IAsyncDisposable {
	private readonly RunnerOptions _options;
	private readonly HeadlessLauncher _launcher;
	// The address workers listen on (loopback), doubling as the host the update flow's control
	// requests (drain / status) connect to.
	private readonly string _workerHost;
	private readonly HttpClient _http = new();
	private readonly object _gate = new();
	private WorkspaceBackend? _backend;

	/// <summary>
	/// Creates a manager that provisions workers per <paramref name="options"/>, reaching each worker's
	/// control endpoints at <paramref name="workerHost"/> (the bind address the launcher spawns them on).
	/// </summary>
	public BackendManager(RunnerOptions options, HeadlessLauncher launcher, string workerHost) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentException.ThrowIfNullOrEmpty(workerHost);
		_options = options;
		_launcher = launcher;
		_workerHost = workerHost;
	}

	/// <summary>The current backend, or <c>null</c> before the first <see cref="Ensure"/> call.</summary>
	public WorkspaceBackend? Current {
		get {
			lock (_gate) {
				return _backend;
			}
		}
	}

	/// <summary>
	/// Returns the workspace backend, starting a fresh worker when none runs (or the previous one tripped its
	/// supervisor breaker). The worker may still be <c>starting</c>; the bridge re-attaches once it is up.
	/// </summary>
	public WorkspaceBackend Ensure() {
		lock (_gate) {
			// Mid-update the swap/rollback owns the lifecycle: re-provisioning here would mint a new
			// token/port and orphan every reconnecting tab, so hand back the backend as-is.
			if (_backend is not null && _updating) {
				return _backend;
			}

			if (_backend is { Supervisor.State: not SupervisorState.Failed }) {
				return _backend;
			}

			_backend?.Supervisor?.Dispose();
			var backend = new WorkspaceBackend {
				WorkspaceRoot = _options.WorkspaceRoot,
				// A pinned port (secured modes) keeps the TLS-front mapping valid across worker restarts; otherwise
				// grab a free one (local use, where nothing fronts a fixed port).
				Port = _options.WorkerPort ?? AllocatePort(),
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

		_http.Dispose();
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
