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
	private readonly string _workerToken;
	private readonly HttpClient _http;
	private readonly object _gate = new();
	private WorkspaceBackend? _backend;

	/// <summary>
	/// Creates a manager that provisions workers per <paramref name="options"/>, reaching each worker's
	/// control endpoints at <paramref name="workerHost"/> (the bind address the launcher spawns them on).
	/// </summary>
	public BackendManager(RunnerOptions options, HeadlessLauncher launcher, string workerHost)
		: this(options, launcher, workerHost, new HttpClient()) { }

	internal BackendManager(RunnerOptions options, HeadlessLauncher launcher, string workerHost, HttpClient http) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentException.ThrowIfNullOrEmpty(workerHost);
		ArgumentNullException.ThrowIfNull(http);
		_options = options;
		_launcher = launcher;
		_workerHost = workerHost;
		_workerToken = WorkerAccessToken.Derive(options.RunnerToken, options.WorkspaceRoot);
		_http = http;
		_healthMonitor = Task.Run(MonitorHealthAsync);
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
	/// Returns the stable workspace backend, creating and starting it only on the first call. A terminal
	/// supervisor stays terminal until explicit update orchestration or runner restart, so polling cannot erase
	/// its breaker. The worker may still be <c>starting</c>; the bridge re-attaches once it is up.
	/// </summary>
	public WorkspaceBackend Ensure() {
		lock (_gate) {
			// One manager owns one stable endpoint and one supervisor history. Browser polling must never
			// replace a terminal supervisor and thereby erase its breaker; update orchestration explicitly
			// Stop/Starts this same object, while a runner process restart constructs a fresh manager.
			if (_backend is not null) {
				return _backend;
			}

			var backend = new WorkspaceBackend {
				WorkspaceRoot = _options.WorkspaceRoot,
				// A pinned port (secured modes) keeps the TLS-front mapping valid across worker restarts; otherwise
				// grab a free one (local use, where nothing fronts a fixed port) — HeadlessLauncher repicks it
				// for any launch that would find another listener already there.
				Port = _options.WorkerPort ?? AllocatePort(),
				PortIsPinned = _options.WorkerPort.HasValue,
				Token = _workerToken,
			};
			backend.Supervisor = _launcher.BuildSupervisor(backend);
			_backend = backend;
			backend.Supervisor.Start();
			return backend;
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		_healthCancellation.Cancel();
		await _healthMonitor.ConfigureAwait(false);
		lock (_gate) {
			_backend?.Supervisor?.Dispose();
			_backend = null;
		}

		_http.Dispose();
		_healthCancellation.Dispose();
	}

	/// <summary>Returns <c>running</c> only after the worker's own control endpoint is ready.</summary>
	public async Task<string> StatusAsync(WorkspaceBackend backend) {
		ArgumentNullException.ThrowIfNull(backend);
		ProcessSupervisor? supervisor;
		long generation;
		lock (_gate) {
			supervisor = backend.Supervisor;
			if (supervisor?.State != SupervisorState.Running) {
				return backend.Status;
			}

			generation = supervisor.Generation;
		}

		var probe = await ProbeStatusAsync(backend, CancellationToken.None).ConfigureAwait(false);
		if (!IsCurrentGeneration(backend, supervisor, generation)) {
			return backend.Status;
		}

		if (probe.State == WorkerStatusProbeState.Pending) {
			return "starting";
		}

		string? failure = probe.State == WorkerStatusProbeState.ProtocolFailure
			? probe.Failure
			: WorkerContractFailure(probe.Status!);
		if (failure is not null) {
			ReplaceUnhealthy(backend, generation, failure);
			return "failed";
		}

		return "running";
	}

	/// <summary>
	/// Grabs a free TCP port by binding to port 0 and releasing it — a reservation, not a hold, so another
	/// process can take it before the worker binds it seconds later. <see cref="HeadlessLauncher"/> checks the
	/// port is still free before each launch and calls this again when it isn't.
	/// </summary>
	internal static int AllocatePort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}
}
