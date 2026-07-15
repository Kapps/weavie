using System.Text;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents;

/// <summary>Composes one provider session with Weavie's terminal or structured runtime host.</summary>
public sealed class AgentSessionHost : IAsyncDisposable {
	private readonly AgentSessionContext _context;
	private readonly IHostBridge _bridge;
	private readonly List<AgentPaneMessage> _paneMessages = [];
	private readonly Dictionary<string, int> _paneItemIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, PaneDeltaBuffer> _paneDeltaBuffers = new(StringComparer.Ordinal);
	private readonly Lock _paneGate = new();
	// Batches live pane messages into fewer bridge frames so a fast turn (or a resumed thread's history replay)
	// can't burst past the bridge's bounded outbox and drop a network-slow page. See AgentPaneCoalescer.
	private readonly AgentPaneCoalescer _coalescer;

	// Durable transcript for the structured pane (null for terminal-backed providers, which repaint themselves).
	// Seeds the replay buffer SYNCHRONOUSLY at construction, so a reconnecting page's ReplayPane restores the pane
	// immediately — before the async thread/resume that HydrateTranscript waits on, which the reconnect races and
	// loses (leaving the pane blank). See docs/specs/agent-pane-persistence.md.
	private readonly AgentPaneTranscriptStore? _transcript;

	/// <summary>Creates the provider session and its pane runtime.</summary>
	public AgentSessionHost(
		IAgentProvider provider,
		AgentSessionContext context,
		IHostBridge bridge,
		SettingsStore settings,
		IPtyLauncher ptyLauncher,
		string transcriptPath) {
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		ArgumentException.ThrowIfNullOrEmpty(transcriptPath);
		_context = context;
		_bridge = bridge;
		_coalescer = new AgentPaneCoalescer(PostPaneBatch, settings.RequireInt(AgentSettings.PaneCoalesceMs));
		Provider = provider.Info;
		Session = provider.CreateSession(context);
		if (Session is ITerminalAgentSession terminalSession) {
			Terminal = new TerminalController(bridge, "claude", settings, ptyLauncher, new AgentTerminalProcess(terminalSession)) {
				Workspace = context.Workspace,
			};
		} else if (Session is IStructuredAgentSession structuredSession) {
			Structured = structuredSession;
			_transcript = new AgentPaneTranscriptStore(context.FileSystem, transcriptPath);
			_transcript.Log += Console.WriteLine;
			// Seed the replay buffer from the persisted transcript BEFORE subscribing, so a reopened session
			// restores its prior output immediately and live messages append after it.
			foreach (var message in _transcript.Snapshot()) {
				StorePaneMessage(message);
			}

			structuredSession.PaneMessage += PublishPaneMessage;
		} else {
			throw new InvalidOperationException($"Provider '{Provider.Id}' returned an unsupported agent session.");
		}

		if (Session is IStructuredAgentControls controls) {
			Controls = controls;
			controls.ControlStateChanged += PublishControlState;
		}
	}

	/// <summary>The selected provider identity.</summary>
	public AgentProviderInfo Provider { get; }

	/// <summary>The live provider session.</summary>
	public IAgentSession Session { get; }

	/// <summary>The provider's compatibility terminal pane, when terminal-backed.</summary>
	public TerminalController? Terminal { get; }

	/// <summary>The provider's structured runtime, when native-pane backed.</summary>
	public IStructuredAgentSession? Structured { get; }

	/// <summary>The provider's live model/approvals/sandbox controls, when it exposes them.</summary>
	public IStructuredAgentControls? Controls { get; }

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		Terminal?.Dispose();
		_coalescer.Dispose();
		await DisposeProviderAsync().ConfigureAwait(false);
	}

	/// <summary>Replays the structured pane state accumulated for this session.</summary>
	public void ReplayPane() {
		// Post under the gate so this replay's reset+messages stay ordered against a concurrent hydrate/resume
		// (another thread): posting outside it let a trailing reset land after hydrate's content and blank the
		// pane on a slow remote cold-start. Under the gate each writer's reset+messages is atomic on the wire.
		lock (_paneGate) {
			List<AgentPaneMessage> snapshot = [.. _paneMessages];
			foreach (var buffer in _paneDeltaBuffers.Values) {
				snapshot[buffer.Index] = buffer.Latest with { Text = buffer.Text.ToString() };
			}

			// Every buffered live message is already in the snapshot (it was stored before it was coalesced), so
			// drop the buffer or the timer would re-post it as a batch after this replay and duplicate it.
			_coalescer.Discard();
			_bridge.PostToWeb(AgentPaneProtocol.Reset(_context.CurrentSessionId(), _context.Workspace));
			_bridge.PostToWeb(AgentPaneProtocol.Batch(_context.CurrentSessionId(), _context.Workspace, snapshot));
		}
	}

	/// <summary>Replays every structured-agent surface owned by this session.</summary>
	public void ReplayState() {
		if (Structured is null) {
			return;
		}

		ReplayPane();
		ReplayControls();
	}

	/// <summary>Replays the current control state, so a (re)connecting web view shows the live model/approvals/sandbox.</summary>
	public void ReplayControls() {
		if (Controls is not null) {
			PublishControlState(Controls.ControlState);
		}
	}

	/// <summary>Disposes provider integration after the terminal has already stopped.</summary>
	public ValueTask DisposeProviderAsync() => Session.DisposeAsync();

	private void PublishControlState(AgentControlState state) =>
		_bridge.PostToWeb(AgentControlsProtocol.Message(_context.CurrentSessionId(), _context.Workspace, state));

	private void PublishPaneMessage(AgentPaneMessage message) {
		// Post inside the gate so this writer's mutation and its web post stay ordered against a concurrent
		// ReplayPane (see the note there); the post is a bridge enqueue, no costlier than the append above it.
		if (message.Type == "transcript-reset") {
			lock (_paneGate) {
				_paneMessages.Clear();
				_paneItemIndexes.Clear();
				_paneDeltaBuffers.Clear();
				_transcript?.Clear();
				// Buffered pre-reset messages are being wiped; drop them so a later flush can't resurrect them.
				_coalescer.Discard();
				_bridge.PostToWeb(AgentPaneProtocol.Reset(_context.CurrentSessionId(), _context.Workspace));
			}
			return;
		}

		// Store and buffer under one lock so a concurrent ReplayPane either sees this message in the snapshot AND
		// discards it from the buffer, or sees neither — never both (which would duplicate it on the wire).
		lock (_paneGate) {
			StorePaneMessage(message);
			_transcript?.Append(message);
			_coalescer.Add(message);
		}
	}

	// The coalescer's sink: a lone message keeps today's `agent-pane` frame; a batch (only ever formed during a
	// burst) becomes one `agent-pane-batch`. Both ingest identically page-side, so this is a pure wire economy.
	private void PostPaneBatch(IReadOnlyList<AgentPaneMessage> messages) => _bridge.PostToWeb(messages.Count == 1
		? AgentPaneProtocol.Message(_context.CurrentSessionId(), _context.Workspace, messages[0])
		: AgentPaneProtocol.Batch(_context.CurrentSessionId(), _context.Workspace, messages));

	private void StorePaneMessage(AgentPaneMessage message) {
		string? key = AgentPaneIdentity.ItemKey(message);
		if (key is null) {
			_paneMessages.Add(message);
			return;
		}

		if (message.Type == "item-started") {
			_paneDeltaBuffers.Remove(key);
			if (_paneItemIndexes.TryGetValue(key, out int startedIndex)) {
				_paneMessages[startedIndex] = message;
			} else {
				_paneItemIndexes[key] = _paneMessages.Count;
				_paneMessages.Add(message);
			}
			return;
		}

		if (IsDelta(message)) {
			if (!_paneItemIndexes.TryGetValue(key, out int deltaIndex)) {
				deltaIndex = _paneMessages.Count;
				_paneItemIndexes[key] = deltaIndex;
				_paneMessages.Add(message with { Text = null });
			}
			if (!_paneDeltaBuffers.TryGetValue(key, out var buffer)) {
				buffer = new PaneDeltaBuffer(deltaIndex, message);
				_paneDeltaBuffers.Add(key, buffer);
			}
			buffer.Latest = message;
			buffer.Text.Append(message.Text);
			return;
		}

		if (message.Type == "item-completed" && _paneItemIndexes.Remove(key, out int completedIndex)) {
			_paneDeltaBuffers.Remove(key);
			_paneMessages[completedIndex] = message;
			return;
		}

		_paneMessages.Add(message);
	}

	private static bool IsDelta(AgentPaneMessage message) =>
		message.Type is "agent-message-delta" or "plan-delta" or "command-output-delta";

	private sealed class PaneDeltaBuffer(int index, AgentPaneMessage latest) {
		public int Index { get; } = index;
		public AgentPaneMessage Latest { get; set; } = latest;
		public StringBuilder Text { get; } = new();
	}

	private sealed class AgentTerminalProcess(ITerminalAgentSession session) : ITerminalProcess {
		public AgentLaunch ResolveLaunch() => session.ResolveLaunch();

		public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) => session.ObserveTerminalOutput(data);

		public void ObserveTerminalInput(ReadOnlyMemory<byte> data) => session.ObserveTerminalInput(data);

		public void ObserveProcessExit(AgentProcessExit exit) => session.ObserveProcessExit(exit);
	}
}
