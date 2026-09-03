using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Agents;

/// <summary>Composes one provider session with Weavie's terminal or structured runtime host.</summary>
public sealed partial class AgentSessionHost : IAsyncDisposable {
	private readonly MessageFeatureChannel _messages;
	private readonly List<AgentPaneMessage> _paneMessages = [];
	private readonly List<long> _paneOrdinals = [];
	private readonly List<long> _paneRevisions = [];
	private readonly Dictionary<string, int> _paneItemIndexes = new(StringComparer.Ordinal);
	private readonly HashSet<string> _paneActiveItems = new(StringComparer.Ordinal);
	private readonly Dictionary<int, PaneDeltaBuffer> _paneDeltaBuffers = [];
	private readonly Dictionary<object, HistoryRead> _historyReads = [];
	private readonly object _directHistoryReader = new();
	private readonly Lock _paneGate = new();
	private readonly AgentPaneOutput _paneOutput;
	private readonly IAgentAuthenticationTerminal _authenticationTerminal;
	private long _paneGeneration;
	private long _nextPaneOrdinal;
	private long _nextPaneRevision;

	/// <summary>Creates the provider session and its pane runtime.</summary>
	public AgentSessionHost(
		IAgentProvider provider,
		AgentSessionContext context,
		MessageFeatureChannel messages,
		MessageFeatureChannel terminalMessages,
		SettingsStore settings,
		IPtyLauncher ptyLauncher) {
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentNullException.ThrowIfNull(terminalMessages);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		_messages = messages;
		_authenticationTerminal = context.AuthenticationTerminal;
		AuthenticationTerminal = context.AuthenticationTerminal as AgentAuthenticationTerminal;
		_paneOutput = new AgentPaneOutput(
			messages,
			settings.RequireInt(AgentSettings.PaneCoalesceMs),
			Console.WriteLine);
		Provider = provider.Info;
		try {
			Session = provider.CreateSession(context);
		} catch {
			_authenticationTerminal.DisposeAsync().AsTask().GetAwaiter().GetResult();
			throw;
		}
		if (Session is ITerminalAgentSession terminalSession) {
			TerminalSession = terminalSession;
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
			structuredSession.PaneMessage += PublishPaneMessage;
			structuredSession.PaneSnapshot += RestorePaneSnapshot;
		} else {
			throw new InvalidOperationException($"Provider '{Provider.Id}' returned an unsupported agent session.");
		}

		if (Session is IStructuredAgentControls controls) {
			Controls = controls;
			controls.ControlStateChanged += PublishControlState;
		}
		if (Session is IStructuredAgentUsage usage) {
			Usage = usage;
			usage.UsageChanged += PublishUsage;
		}
		SideConversations = Session as IStructuredAgentSideConversations;
	}

	/// <summary>The selected provider identity.</summary>
	public AgentProviderInfo Provider { get; }

	/// <summary>The live provider session.</summary>
	public IAgentSession Session { get; }

	/// <summary>The provider's compatibility terminal pane, when terminal-backed.</summary>
	public TerminalController? Terminal { get; }

	/// <summary>The provider's terminal-facing session, when terminal-backed.</summary>
	public ITerminalAgentSession? TerminalSession { get; }

	/// <summary>The provider's structured runtime, when native-pane backed.</summary>
	public IStructuredAgentSession? Structured { get; }

	/// <summary>The provider's live model/approvals/sandbox controls, when it exposes them.</summary>
	public IStructuredAgentControls? Controls { get; }

	/// <summary>The provider's live context-window and usage-limit reporting, when it exposes it.</summary>
	public IStructuredAgentUsage? Usage { get; }

	/// <summary>The context-preserving side-conversation surface, when the structured agent supports it.</summary>
	public IStructuredAgentSideConversations? SideConversations { get; }

	internal AgentAuthenticationTerminal? AuthenticationTerminal { get; }

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		Terminal?.Dispose();
		try {
			await DisposeProviderAsync().ConfigureAwait(false);
		} finally {
			await _authenticationTerminal.DisposeAsync().ConfigureAwait(false);
		}
		if (Structured is { } structured) {
			structured.PaneMessage -= PublishPaneMessage;
			structured.PaneSnapshot -= RestorePaneSnapshot;
		}
		if (Controls is { } controls) {
			controls.ControlStateChanged -= PublishControlState;
		}
		if (Usage is { } usage) {
			usage.UsageChanged -= PublishUsage;
		}
		await _paneOutput.DisposeAsync().ConfigureAwait(false);
	}

	/// <summary>Replays every structured-agent surface owned by this session.</summary>
	public void ReplayState() => ReplayState(_messages);

	internal void ReplayState(MessageTargetFeature messages) =>
		ReplayState((IMessageFeatureTarget)messages);

	internal Task DrainPaneAsync(CancellationToken ct) => _paneOutput.DrainAsync(ct);

	private void ReplayState(IMessageFeatureTarget messages) {
		if (AuthenticationTerminal is { } authenticationTerminal) {
			messages.Publish("authenticationTerminal", new { active = authenticationTerminal.Active });
		}
		if (Structured is null) {
			return;
		}

		ReplayControls(messages);
		ReplayUsage(messages);
	}

	/// <summary>Replays the current control state, so a (re)connecting web view shows the live model/approvals/sandbox.</summary>
	public void ReplayControls() => ReplayControls(_messages);

	private void ReplayControls(IMessageFeatureTarget messages) {
		if (Controls is not null) {
			messages.Publish("controls", AgentControlsProtocol.Message(Controls.ControlState));
		}
	}

	private void ReplayUsage(IMessageFeatureTarget messages) {
		if (Usage is not null) {
			messages.Publish("usage", AgentUsageProtocol.Message(Usage.Snapshot));
		}
	}

	/// <summary>Disposes provider integration after the terminal has already stopped.</summary>
	public ValueTask DisposeProviderAsync() => Session.DisposeAsync();

	private void PublishControlState(AgentControlState state) =>
		_messages.Publish("controls", AgentControlsProtocol.Message(state));

	private void PublishUsage(AgentUsageSnapshot usage) =>
		_messages.Publish("usage", AgentUsageProtocol.Message(usage));

	private sealed class AgentTerminalProcess(ITerminalAgentSession session) : ITerminalProcess {
		public AgentLaunch ResolveLaunch() => session.ResolveLaunch();

		public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) => session.ObserveTerminalOutput(data);

		public void ObserveTerminalInput(ReadOnlyMemory<byte> data) => session.ObserveTerminalInput(data);

		public void ObserveProcessExit(AgentProcessExit exit) => session.ObserveProcessExit(exit);
	}
}
