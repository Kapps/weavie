using System.Text;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Hosting.Agents;

/// <summary>Composes one provider session with Weavie's terminal or structured runtime host.</summary>
public sealed class AgentSessionHost : IAsyncDisposable {
	private readonly AgentSessionContext _context;
	private readonly IHostBridge _bridge;
	private readonly List<AgentPaneMessage> _paneMessages = [];
	private readonly Dictionary<string, int> _paneItemIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, PaneDeltaBuffer> _paneDeltaBuffers = new(StringComparer.Ordinal);
	private readonly Lock _paneGate = new();

	/// <summary>Creates the provider session and its pane runtime.</summary>
	public AgentSessionHost(
		IAgentProvider provider,
		AgentSessionContext context,
		IHostBridge bridge,
		SettingsStore settings,
		IPtyLauncher ptyLauncher) {
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
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
			foreach (var buffer in _paneDeltaBuffers.Values) {
				snapshot[buffer.Index] = buffer.Latest with { Text = buffer.Text.ToString() };
			}
		}

		_bridge.PostToWeb(AgentPaneProtocol.Reset(_context.CurrentSessionId(), _context.Workspace));
		foreach (var message in snapshot) {
			_bridge.PostToWeb(AgentPaneProtocol.Message(_context.CurrentSessionId(), _context.Workspace, message));
		}
	}

	/// <summary>Disposes provider integration after the terminal has already stopped.</summary>
	public ValueTask DisposeProviderAsync() => Session.DisposeAsync();

	private void PublishPaneMessage(AgentPaneMessage message) {
		if (message.Type == "transcript-reset") {
			lock (_paneGate) {
				_paneMessages.Clear();
				_paneItemIndexes.Clear();
				_paneDeltaBuffers.Clear();
			}
			_bridge.PostToWeb(AgentPaneProtocol.Reset(_context.CurrentSessionId(), _context.Workspace));
			return;
		}
		lock (_paneGate) {
			StorePaneMessage(message);
		}

		_bridge.PostToWeb(AgentPaneProtocol.Message(_context.CurrentSessionId(), _context.Workspace, message));
	}

	private void StorePaneMessage(AgentPaneMessage message) {
		string? key = ItemKey(message);
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

	private static string? ItemKey(AgentPaneMessage message) =>
		string.IsNullOrEmpty(message.ItemId) ? null : $"{message.TurnId ?? "session"}:{message.ItemId}";

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
