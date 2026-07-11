using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents;

/// <summary>Composes one provider session with Weavie's terminal or structured runtime host.</summary>
public sealed class AgentSessionHost : IAsyncDisposable {
	// Emitted by a structured provider when it abandons its thread (a fresh thread starts): drop the stale
	// transcript rather than replay history that belongs to a thread the resumed session no longer knows.
	private const string ThreadResetType = "thread-reset";

	private readonly AgentSessionContext _context;
	private readonly IHostBridge _bridge;
	private readonly List<AgentPaneMessage> _paneMessages = [];
	private readonly Lock _paneGate = new();

	// Durable transcript for the structured pane (null for terminal-backed providers, which repaint themselves).
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
			// Seed the replay buffer with the persisted transcript BEFORE subscribing, so a reopened session
			// restores its prior output and live messages append after it.
			_paneMessages.AddRange(_transcript.Snapshot());
			structuredSession.PaneMessage += PublishPaneMessage;
		} else {
			throw new InvalidOperationException($"Provider '{Provider.Id}' returned an unsupported agent session.");
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

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		Terminal?.Dispose();
		await DisposeProviderAsync().ConfigureAwait(false);
	}

	/// <summary>Replays the structured pane state accumulated for this session.</summary>
	public void ReplayPane() {
		List<AgentPaneMessage> snapshot;
		lock (_paneGate) {
			snapshot = [.. _paneMessages];
		}

		_bridge.PostToWeb(AgentPaneProtocol.Reset(_context.CurrentSessionId(), _context.Workspace));
		foreach (var message in snapshot) {
			_bridge.PostToWeb(AgentPaneProtocol.Message(_context.CurrentSessionId(), _context.Workspace, message));
		}
	}

	/// <summary>Disposes provider integration after the terminal has already stopped.</summary>
	public ValueTask DisposeProviderAsync() => Session.DisposeAsync();

	private void PublishPaneMessage(AgentPaneMessage message) {
		if (message.Type == ThreadResetType) {
			ResetTranscript();
			return;
		}

		lock (_paneGate) {
			_paneMessages.Add(message);
			_transcript?.Append(message);
		}

		_bridge.PostToWeb(AgentPaneProtocol.Message(_context.CurrentSessionId(), _context.Workspace, message));
	}

	private void ResetTranscript() {
		lock (_paneGate) {
			_paneMessages.Clear();
			_transcript?.Clear();
		}

		_bridge.PostToWeb(AgentPaneProtocol.Reset(_context.CurrentSessionId(), _context.Workspace));
	}

	private sealed class AgentTerminalProcess(ITerminalAgentSession session) : ITerminalProcess {
		public AgentLaunch ResolveLaunch() => session.ResolveLaunch();

		public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) => session.ObserveTerminalOutput(data);

		public void ObserveTerminalInput(ReadOnlyMemory<byte> data) => session.ObserveTerminalInput(data);

		public void ObserveProcessExit(AgentProcessExit exit) => session.ObserveProcessExit(exit);
	}
}
