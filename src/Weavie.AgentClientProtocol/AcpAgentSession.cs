using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>One worktree-scoped ACP session rendered in Weavie's native pane: a process and its conversations.</summary>
public sealed partial class AcpAgentSession :
	IStructuredAgentSession,
	IStructuredAgentControls,
	IStructuredAgentUsage,
	IStructuredAgentSideConversations {
	private readonly AgentSessionContext _context;
	private readonly Func<AcpAgentDefinition> _definitionSource;
	private AcpAgentDefinition _definition;
	private readonly AcpSessionStore _sessions;
	private readonly AcpControlStore _controlDefaults;
	private readonly Action<string> _log;
	private readonly AcpJsonRpcConnection _connection;
	private readonly AcpConversationHost _host;
	private readonly Lock _gate = new();
	private readonly Lock _turnTransitionGate = new();
	private readonly Dictionary<string, SideRuntime> _sides = new(StringComparer.Ordinal);
	private AcpConversation _primary;
	private AcpAgentFeatures _features = AcpAgentFeatures.None;
	private long _processGeneration;
	private AcpProcess? _process;
	private bool _started;
	private bool _disposed;

	/// <summary>Creates a supervised ACP session.</summary>
	public AcpAgentSession(
		AgentSessionContext context,
		AcpAgentDefinition definition,
		AcpSessionStore sessions,
		AcpControlStore controlDefaults,
		Action<string> log) : this(context, () => definition, sessions, controlDefaults, log) { }

	internal AcpAgentSession(
		AgentSessionContext context,
		Func<AcpAgentDefinition> definition,
		AcpSessionStore sessions,
		AcpControlStore controlDefaults,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(controlDefaults);
		ArgumentNullException.ThrowIfNull(log);
		_context = context;
		_definitionSource = definition;
		_definition = definition() ?? throw new InvalidOperationException("The ACP agent definition is unavailable.");
		_sessions = sessions;
		_controlDefaults = controlDefaults;
		_log = log;
		_connection = new AcpJsonRpcConnection(ResolveDefinition, context.Workspace, log);
		_host = new AcpConversationHost(context, () => _definition, log, _turnTransitionGate);
		_primary = CreatePrimary(AcpConversationHandoff.Fresh(NewContinuation(string.Empty, 0, string.Empty, guidanceSent: false)));
		_connection.ProcessStarted += OnProcessStarted;
		_connection.ProcessStateChanged += change => {
			lock (_turnTransitionGate) _primary.Observe(new AgentProcessChanged(change));
		};
		_connection.NotificationReceived += (generation, root) => {
			lock (_turnTransitionGate) {
				if (generation == _processGeneration) _primary.HandleNotification(root);
			}
		};
		_connection.RequestReceived += request => {
			lock (_turnTransitionGate) {
				if (request.Generation == _processGeneration) _primary.RegisterClientRequest(request);
				else _connection.RejectClosedRequest(request);
			}
		};
		_connection.ProtocolFaulted += OnProtocolFault;
	}

	private AcpAgentDefinition ResolveDefinition() {
		var definition = _definitionSource()
			?? throw new InvalidOperationException("The ACP agent definition is unavailable.");
		if (!string.Equals(definition.Id, _definition.Id, StringComparison.Ordinal)) {
			throw new InvalidOperationException("An ACP agent definition cannot change provider identity.");
		}
		_definition = definition;
		return definition;
	}

	/// <inheritdoc/>
	public event Action<AgentPaneMessage>? PaneMessage;

	/// <inheritdoc/>
	public event Action<IReadOnlyList<AgentPaneMessage>>? PaneSnapshot;

	/// <inheritdoc/>
	public event Action<IReadOnlyList<AgentTurnSubmission>>? QueuedSubmissionsChanged;

	/// <inheritdoc/>
	public event Action<AgentControlState>? ControlStateChanged;

	/// <inheritdoc/>
	public event Action<AgentUsageSnapshot>? UsageChanged;

	/// <inheritdoc/>
	public IReadOnlyList<AgentTurnSubmission> QueuedSubmissions => Primary.QueuedSubmissions;

	/// <inheritdoc/>
	public AgentControlState ControlState {
		get {
			var snapshot = Primary.Snapshot;
			bool forkable = snapshot.Ready && Forkable;
			bool rewindable = forkable && snapshot.TurnNumber > 0;
			return new AgentControlState {
				Ready = snapshot.Ready,
				Rewindable = rewindable,
				Axes = snapshot.Axes,
				Slash = AgentControlCommands.ComposeSlash(snapshot.Commands, forkable, rewindable),
			};
		}
	}

	/// <inheritdoc/>
	public AgentUsageSnapshot Snapshot => Primary.Usage;

	private AcpConversation Primary {
		get { lock (_gate) return _primary; }
	}

	private bool Forkable {
		get { lock (_gate) return _features is { Fork: true, Load: true }; }
	}

	private SideRuntime[] Sides() {
		lock (_gate) return [.. _sides.Values];
	}

	private AcpConversation CreatePrimary(AcpConversationHandoff handoff) => new(
		_host,
		new AcpConversationSpec(handoff with { Continuation = Untouched(handoff.Continuation) }, AcpConversationOpening.Continue, SideScoped: false),
		new PrimaryPort(this));

	// A primary with no turns has no conversation to resume; a side can inherit history before its first turn.
	private static AcpConversationState Untouched(AcpConversationState continuation) =>
		continuation.TurnNumber == 0 ? continuation with { SessionId = null } : continuation;

	// A successor shares the running, initialized process only when the agent can close the predecessor's settled
	// session; otherwise only stopping the process stops its work or an opening that may never return.
	private AcpProcess? ReplaceableProcess(AcpConversation predecessor) {
		lock (_gate) return _features.Close && !predecessor.Failed && !predecessor.Opening ? _process : null;
	}

	/// <summary>Retires the predecessor and installs its successor; without a process it waits for <see cref="Launch"/> to restart.</summary>
	private AcpConversation Succeed(AcpConversation predecessor, AcpProcess? process, Func<AcpConversationHandoff, AcpConversation> successor) {
		var next = successor(process is null ? predecessor.Retire() : predecessor.RetireClosing());
		lock (_gate) {
			_primary = next;
			if (process is null) (_processGeneration, _process) = (0, null);
		}
		return next;
	}

	/// <summary>Opens the installed <paramref name="primary"/> on <paramref name="process"/>, or restarts the process, which attaches it.</summary>
	private void Launch(AcpConversation primary, AcpProcess? process) {
		if (process is null) _connection.Restart();
		else Open(primary, process);
	}

	private void Open(AcpConversation conversation, AcpProcess process) {
		conversation.Attach(process);
		conversation.RunRuntime(() => conversation.OpenAsync(Features));
	}

	private static AcpConversationState NewContinuation(
		string conversationId, long anchorTurnNumber, string initialPrompt, bool guidanceSent) => new() {
			ConversationId = conversationId,
			SessionId = null,
			AnchorTurnNumber = anchorTurnNumber,
			InitialPrompt = initialPrompt,
			TurnNumber = 0,
			GuidanceSent = guidanceSent,
			PlanTurns = new Dictionary<string, string>(StringComparer.Ordinal),
			Failed = false,
		};
}
