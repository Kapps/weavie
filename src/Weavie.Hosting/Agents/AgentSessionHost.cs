using System.Text;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Agents;

/// <summary>Composes one provider session with Weavie's terminal or structured runtime host.</summary>
public sealed class AgentSessionHost : IAsyncDisposable {
	private readonly MessageFeatureChannel _messages;
	private readonly List<AgentPaneMessage> _paneMessages = [];
	private readonly Dictionary<string, int> _paneItemIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, PaneDeltaBuffer> _paneDeltaBuffers = new(StringComparer.Ordinal);
	private readonly Lock _paneGate = new();
	private readonly AgentPaneOutput _paneOutput;
	private readonly AgentPaneJournal? _paneJournal;
	private long _paneGeneration;

	/// <summary>Creates the provider session and its pane runtime.</summary>
	public AgentSessionHost(
		IAgentProvider provider,
		AgentSessionContext context,
		MessageFeatureChannel messages,
		MessageFeatureChannel terminalMessages,
		SettingsStore settings,
		IPtyLauncher ptyLauncher,
		string transcriptPath) {
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentNullException.ThrowIfNull(terminalMessages);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		ArgumentException.ThrowIfNullOrEmpty(transcriptPath);
		_messages = messages;
		_paneOutput = new AgentPaneOutput(
			messages,
			settings.RequireInt(AgentSettings.PaneCoalesceMs),
			Console.WriteLine);
		Provider = provider.Info;
		Session = provider.CreateSession(context);
		if (Session is ITerminalAgentSession terminalSession) {
			Terminal = new TerminalController(
				terminalMessages,
				"agent",
				settings,
				ptyLauncher,
				new AgentTerminalProcess(terminalSession)) {
				Workspace = context.Workspace,
			};
		} else if (Session is IStructuredAgentSession structuredSession) {
			Structured = structuredSession;
			_paneJournal = new AgentPaneJournal(
				context.FileSystem,
				transcriptPath,
				SeedPersistedPane,
				Console.WriteLine);
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
		await DisposeProviderAsync().ConfigureAwait(false);
		if (Structured is { } structured) {
			structured.PaneMessage -= PublishPaneMessage;
		}
		if (Controls is { } controls) {
			controls.ControlStateChanged -= PublishControlState;
		}
		if (_paneJournal is { } journal) {
			await journal.DisposeAsync().ConfigureAwait(false);
		}
		await _paneOutput.DisposeAsync().ConfigureAwait(false);
	}

	/// <summary>Replays the structured pane state accumulated for this session.</summary>
	public void ReplayPane() => ReplayPane(_messages);

	internal void ReplayPane(MessageTargetFeature messages) =>
		ReplayPane((IMessageFeatureTarget)messages);

	private void ReplayPane(IMessageFeatureTarget messages) {
		lock (_paneGate) {
			_paneOutput.Replay(messages, PaneSnapshotLocked());
		}
	}

	/// <summary>Replays every structured-agent surface owned by this session.</summary>
	public void ReplayState() => ReplayState(_messages);

	internal void ReplayState(MessageTargetFeature messages) =>
		ReplayState((IMessageFeatureTarget)messages);

	internal async Task DrainPaneAsync(CancellationToken ct) {
		if (_paneJournal is { } journal) {
			await journal.DrainAsync(ct).ConfigureAwait(false);
		}
		await _paneOutput.DrainAsync(ct).ConfigureAwait(false);
	}

	internal Task WaitForPaneReadyAsync(CancellationToken ct) =>
		_paneJournal?.WaitUntilReadyAsync(ct) ?? Task.CompletedTask;

	private void ReplayState(IMessageFeatureTarget messages) {
		if (Structured is null) {
			return;
		}

		ReplayPane(messages);
		ReplayControls(messages);
	}

	/// <summary>Replays the current control state, so a (re)connecting web view shows the live model/approvals/sandbox.</summary>
	public void ReplayControls() => ReplayControls(_messages);

	private void ReplayControls(IMessageFeatureTarget messages) {
		if (Controls is not null) {
			messages.Publish("controls", AgentControlsProtocol.Message(Controls.ControlState));
		}
	}

	/// <summary>Disposes provider integration after the terminal has already stopped.</summary>
	public ValueTask DisposeProviderAsync() => Session.DisposeAsync();

	internal bool TryGetCompletedPlan(string threadId, string turnId, string itemId, out AgentPlan plan) {
		plan = default;
		if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(itemId)) {
			return false;
		}

		string key = AgentPaneIdentity.ItemKey(threadId, turnId, itemId)!;
		lock (_paneGate) {
			// A fresh stream for this identity supersedes its completed version until it reaches its own final item.
			if (_paneItemIndexes.ContainsKey(key)) {
				return false;
			}

			for (int index = _paneMessages.Count - 1; index >= 0; index--) {
				var message = _paneMessages[index];
				if (!string.Equals(AgentPaneIdentity.ItemKey(message), key, StringComparison.Ordinal)) {
					continue;
				}

				if (message.Type != "item-completed"
					|| !string.Equals(message.ItemType, "plan", StringComparison.Ordinal)
					|| string.IsNullOrWhiteSpace(message.Text)) {
					return false;
				}

				plan = new AgentPlan(key, "Plan", message.Text);
				return true;
			}
		}

		return false;
	}

	private void PublishControlState(AgentControlState state) =>
		_messages.Publish("controls", AgentControlsProtocol.Message(state));

	private void PublishPaneMessage(AgentPaneMessage message) {
		if (message.Type == "transcript-reset") {
			lock (_paneGate) {
				_paneGeneration++;
				_paneMessages.Clear();
				_paneItemIndexes.Clear();
				_paneDeltaBuffers.Clear();
				_paneJournal?.Clear();
				_paneOutput.Reset();
			}
			return;
		}

		lock (_paneGate) {
			StorePaneMessage(message);
			_paneJournal?.Append(message);
			_paneOutput.Live(message);
		}
	}

	private void SeedPersistedPane(IReadOnlyList<AgentPaneMessage> persisted) {
		if (persisted.Count == 0) {
			return;
		}

		lock (_paneGate) {
			if (_paneGeneration != 0) {
				return;
			}

			var live = _paneMessages.ToArray();
			_paneMessages.Clear();
			_paneItemIndexes.Clear();
			_paneDeltaBuffers.Clear();
			foreach (var message in persisted) {
				StorePaneMessage(message);
			}
			foreach (var message in live) {
				StorePaneMessage(message);
			}

			_paneOutput.Replay(_messages, PaneSnapshotLocked());
		}
	}

	private List<AgentPaneMessage> PaneSnapshotLocked() {
		List<AgentPaneMessage> snapshot = [.. _paneMessages];
		foreach (var buffer in _paneDeltaBuffers.Values) {
			snapshot[buffer.Index] = buffer.Latest with { Text = buffer.Text.ToString() };
		}

		return snapshot;
	}

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
