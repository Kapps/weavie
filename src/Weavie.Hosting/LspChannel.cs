using Weavie.Core.Lsp;
using Weavie.Core.Processes;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// One language server bound to a web-minted channel: spawns the server under a <see cref="ProcessSupervisor"/>
/// (<see cref="RestartPolicy.Never"/> — restart is the client's job, since a restarted server needs a fresh
/// <c>initialize</c>/re-<c>didOpen</c> only <c>monaco-languageclient</c> can drive), streams its stdout frames to
/// the page as <c>lsp/data</c>, and reports a self-exit as <c>lsp/exit</c>. The <see cref="LspController"/>'s
/// per-channel unit. Disposing stops and reaps the server (the worktree-removal guarantee).
/// </summary>
internal sealed class LspChannel : IAsyncDisposable {
	private readonly MessageTargetFeature _messages;
	private readonly string _channel;
	private readonly ResolvedCommand _command;
	private readonly string _workspaceRoot;
	private readonly ILspServerLauncher _launcher;
	private readonly Action<string> _log;
	private readonly Action _onClosed;
	private readonly ProcessSupervisor _supervisor;
	private readonly Lock _gate = new();
	private ILspServerProcess? _live;

	public LspChannel(
		MessageTargetFeature messages,
		string channel,
		ResolvedCommand command,
		string workspaceRoot,
		ILspServerLauncher launcher,
		Action<string> log,
		Action<ProcessStopOutcome> forceStopped,
		Action onClosed) {
		_messages = messages;
		_channel = channel;
		_command = command;
		_workspaceRoot = workspaceRoot;
		_launcher = launcher;
		_log = log;
		_onClosed = onClosed;
		_supervisor = new ProcessSupervisor(
			$"lsp:{channel}", StartServer, StopServerAsync,
			new SupervisionOptions { Policy = RestartPolicy.Never }, LogSupervisor, clock: null);
		_supervisor.StateChanged += OnSupervisorStateChanged;
		_supervisor.Stopped += forceStopped;
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

	private void StartServer(SupervisedLaunch launch) {
		var proc = _launcher.Start(_command, _workspaceRoot, _log);
		proc.FrameReceived += OnFrame;
		proc.Exited += launch.NotifyExited;
		lock (_gate) {
			_live = proc;
		}

		proc.Start();
	}

	private Task<ProcessStopOutcome> StopServerAsync() {
		ILspServerProcess? proc;
		lock (_gate) {
			proc = _live;
			_live = null;
		}

		return proc?.StopAsync() ?? Task.FromResult(ProcessStopOutcome.Graceful);
	}

	private void OnFrame(byte[] frame) => _messages.Publish("data", WireJson.Default.LspDataWire, LspMessages.Data(_channel, frame));

	private void OnSupervisorStateChanged(SupervisorStateChanged change) {
		// A non-null exit code means the server ended on its own (crash, clean exit, or crash-loop give-up): reap
		// the dead instance (cancels its pumps), tell the page so its client tears down and — if a document still
		// needs it — reconnects on a fresh channel, and drop ourselves from the hub. An intentional Stop/Dispose
		// carries a null code and emits nothing (the page initiated it).
		if (change.ExitCode is not int code || change.State is not (SupervisorState.Idle or SupervisorState.Failed)) {
			return;
		}

		_ = StopServerAsync();
		_messages.Publish("exit", WireJson.Default.LspExitWire, LspMessages.Exit(_channel, code, null));
		_onClosed();
	}

	private void LogSupervisor(SupervisorLogEntry entry) => _log($"{entry.Name}: {entry.Message}");

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => _supervisor.DisposeAsync();

	/// <summary>
	/// Stops the server AND posts <c>lsp/exit</c> — for a teardown the owning page did NOT initiate (a newer
	/// page instance reaped this channel), so a still-live sibling client learns instead of waiting silently.
	/// </summary>
	public async Task DisposeWithExitAsync(string reason) {
		await DisposeAsync().ConfigureAwait(false);
		_messages.Publish("exit", WireJson.Default.LspExitWire, LspMessages.Exit(_channel, -1, reason));
	}
}
