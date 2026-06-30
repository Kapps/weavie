using Weavie.Core.Lsp;
using Weavie.Core.Processes;

namespace Weavie.Hosting;

/// <summary>
/// One language server bound to a web-minted channel: spawns the server under a <see cref="ProcessSupervisor"/>
/// (<see cref="RestartPolicy.Never"/> — restart is the client's job, since a restarted server needs a fresh
/// <c>initialize</c>/re-<c>didOpen</c> only <c>monaco-languageclient</c> can drive), streams its stdout frames to
/// the page as <c>lsp-data</c>, and reports a self-exit as <c>lsp-exit</c>. The <see cref="LspController"/>'s
/// per-channel unit. Disposing kills and reaps the server (the worktree-removal guarantee).
/// </summary>
internal sealed class LspChannel : IDisposable {
	private readonly IHostBridge _bridge;
	private readonly string _slot;
	private readonly string _channel;
	private readonly ResolvedCommand _command;
	private readonly string _workspaceRoot;
	private readonly ILspServerLauncher _launcher;
	private readonly Action<string> _log;
	private readonly Action<string> _onClosed;
	private readonly ProcessSupervisor _supervisor;
	private readonly Lock _gate = new();
	private ILspServerProcess? _live;

	public LspChannel(
		IHostBridge bridge,
		string slot,
		string channel,
		ResolvedCommand command,
		string workspaceRoot,
		ILspServerLauncher launcher,
		Action<string> log,
		Action<string> onClosed) {
		_bridge = bridge;
		_slot = slot;
		_channel = channel;
		_command = command;
		_workspaceRoot = workspaceRoot;
		_launcher = launcher;
		_log = log;
		_onClosed = onClosed;
		_supervisor = new ProcessSupervisor(
			$"lsp:{channel}", StartServer, StopServer,
			new SupervisionOptions { Policy = RestartPolicy.Never }, LogSupervisor, clock: null);
		_supervisor.StateChanged += OnSupervisorStateChanged;
	}

	/// <summary>Spawns the server.</summary>
	public void Start() => _supervisor.Start();

	/// <summary>Forwards one JSON-RPC payload from the page to the server's stdin (ordered; no-op once it has exited).</summary>
	public void Write(ReadOnlyMemory<byte> payload) {
		ILspServerProcess? live;
		lock (_gate) {
			live = _live;
		}

		live?.Write(payload);
	}

	private void StartServer(int attempt) {
		var proc = _launcher.Start(_command, _workspaceRoot, _log);
		proc.FrameReceived += OnFrame;
		proc.Exited += _supervisor.NotifyExited;
		lock (_gate) {
			_live = proc;
		}

		proc.Start();
	}

	private void StopServer() {
		ILspServerProcess? proc;
		lock (_gate) {
			proc = _live;
			_live = null;
		}

		proc?.Dispose();
	}

	private void OnFrame(byte[] frame) => _bridge.PostToWeb(LspMessages.Data(_slot, _channel, frame));

	private void OnSupervisorStateChanged(SupervisorStateChanged change) {
		// A non-null exit code means the server ended on its own (crash, clean exit, or crash-loop give-up): dispose
		// the dead instance (cancels its pumps), tell the page so its client tears down and — if a document still
		// needs it — reconnects on a fresh channel, and drop ourselves from the hub. An intentional Stop/Dispose
		// carries a null code and emits nothing (the page initiated it).
		if (change.ExitCode is not int code || change.State is not (SupervisorState.Idle or SupervisorState.Failed)) {
			return;
		}

		StopServer();
		_bridge.PostToWeb(LspMessages.Exit(_slot, _channel, code, null));
		_onClosed(_channel);
	}

	private void LogSupervisor(SupervisorLogEntry entry) => _log($"{entry.Name}: {entry.Message}");

	/// <inheritdoc/>
	public void Dispose() => _supervisor.Dispose();
}
