using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>One worktree-scoped ACP session rendered in Weavie's native pane: a process and its conversations.</summary>
public sealed partial class AcpAgentSession :
	IStructuredAgentSession,
	IStructuredAgentControls,
	IStructuredAgentUsage,
	IStructuredAgentSideConversations,
	IStructuredAgentCards,
	IStructuredAgentBackgroundWork {
	private readonly AgentSessionContext _context;
	private AcpAgentDefinition _definition => _runtime.Definition;
	private readonly AcpSessionStore _sessions;
	private readonly AcpRuntime _runtime;
	private Lock _turnTransitionGate => _runtime.TransitionGate;
	private readonly Dictionary<string, AcpConversationHandle> _sides = new(StringComparer.Ordinal);
	private AcpConversation _primary;
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
		_sessions = sessions;
		_runtime = new AcpRuntime(context, definition, controlDefaults, log, QueueBackground);
		_primary = CreatePrimary(AcpConversationHandoff.Fresh(NewContinuation(string.Empty, 0, string.Empty, guidanceSent: false)));
		_runtime.Select(_primary);
	}

	/// <summary>The session-owned shared output.</summary>
	public ConversationOutput Output => _context.Output;

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
		get { lock (_turnTransitionGate) return _primary; }
	}

	private bool Forkable {
		get { lock (_turnTransitionGate) return _runtime.Features is { Fork: true, Load: true }; }
	}

	private AcpConversationHandle[] Sides() {
		lock (_turnTransitionGate) return [.. _sides.Values];
	}

	private AcpConversation CreatePrimary(AcpConversationHandoff handoff) => _runtime.Create(
		new AcpConversationSpec(handoff with { Continuation = Untouched(handoff.Continuation) }, AcpConversationOpening.Continue, AcpConversationRole.Primary),
		new PrimaryPort(this), _context.Bindings.Main());

	// A primary with no turns has no conversation to resume; a side can inherit history before its first turn.
	private static AcpConversationState Untouched(AcpConversationState continuation) =>
		continuation.TurnNumber == 0 ? continuation with { SessionId = null } : continuation;

	// A successor shares the running, initialized process only when the agent can close the predecessor's settled
	// session and nothing runs in the background; otherwise only stopping the process stops that work, or an opening
	// that may never return.
	private AcpProcess? ReplaceableProcess(AcpConversation predecessor) {
		bool background = BackgroundWork.Any(item => item.Running);
		lock (_turnTransitionGate) return _runtime.Features.Close && !predecessor.Failed && !predecessor.Opening && !background ? _runtime.Process : null;
	}

	/// <summary>Retires the predecessor and installs its successor; without a process it waits for <see cref="Launch"/> to restart.</summary>
	private AcpConversation Succeed(AcpConversation predecessor, Func<AcpConversationHandoff, AcpConversation> successor) {
		var next = successor(predecessor.Retire());
		lock (_turnTransitionGate) {
			_primary = next;
			_runtime.Select(next);
		}
		QueueBackground();
		return next;
	}

	/// <summary>Opens the installed <paramref name="primary"/> on <paramref name="process"/>, or restarts the process, which attaches it.</summary>
	private void Launch(AcpConversation primary, AcpProcess? process) {
		if (process is null) _runtime.Restart(primary);
		else _runtime.Open(primary, process);
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
