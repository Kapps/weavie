using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>One ACP conversation, primary or side, reaching its owner only through its port.</summary>
internal sealed partial class AcpConversation {
	private readonly AgentSessionContext _context;
	private readonly Func<AcpAgentDefinition> _definition;
	private readonly Action<string> _log;
	private readonly AcpConversationPort _port;
	private readonly AcpConversationSpec _spec;
	private readonly AgentConversationBinding _binding;
	private readonly bool _ownsBinding;
	private readonly Action<AcpConversation> _retired;
	private readonly TaskCompletionSource<Exception?> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<AgentConversationOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task? _closing;
	private Task? _revocation;
	private readonly AcpOnce<AcpSessionEndpoint> _endpoint = new();
	private readonly CancellationTokenSource _lifetime = new();
	private readonly AcpTerminalManager _terminals;
	private readonly Lock _gate = new();
	private readonly Lock _turnTransitionGate;
	private readonly AcpSubmissionQueue _pendingSubmissions = new();
	private readonly Queue<AcpControlRequest> _controlMutations = [];
	private readonly ConcurrentDictionary<string, AcpClientRequestState> _clientRequests = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, AcpPendingRequest> _pendingRequests = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, string> _urlElicitations = new(StringComparer.Ordinal);
	private readonly HashSet<string> _resolvedRequests = new(StringComparer.Ordinal);
	private readonly Dictionary<string, AcpToolState> _tools = new(StringComparer.Ordinal);
	private readonly HashSet<string> _activeTools = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _planTurns = new(StringComparer.Ordinal);
	private readonly Dictionary<string, HashSet<string>> _turnItemIds = new(StringComparer.Ordinal);
	private readonly Dictionary<string, AgentControlAxis> _controls = new(StringComparer.Ordinal);
	private readonly Queue<AgentPaneMessage> _pendingTerminalMessages = new();
	private readonly RewindReplay _replay = new();
	private IReadOnlyList<AgentSlashEntry> _commands = [];
	private AcpAgentFeatures _features = AcpAgentFeatures.None;
	private string? _sessionId;
	private long _turnNumber;
	private long _publishedQueueVersion;
	private bool _ready;
	private bool _disposed;
	private bool _promptActive;
	// Completion publication still owns the slot after the prompt response, including reentrant submissions.
	private bool _promptDeliveryActive;
	private bool _steering;
	private bool _steeringRefused;
	private bool _loadingTranscript;
	private bool _sessionOpening;
	private bool _waitingForBackground;
	private bool _authenticationPending;
	private bool _authenticating;
	private bool _authenticationOpensSession;
	private CancellationTokenSource? _authenticationCancellation;
	private string? _authenticationItemId;
	private long _authenticationSequence;
	private bool _guidanceSent;
	private bool _runtimeFailed;
	private bool _cancelRequested;
	private bool _controlMutationActive;
	private bool _configOwnsMode;
	private bool _rewinding;
	private AgentContextWindowUsage? _contextUsage;
	private readonly Dictionary<string, AgentUsageLimit> _usageLimits = [];

	internal AcpConversation(AcpConversationHost host, AcpConversationSpec spec, AcpConversationPort port, AgentConversationBinding binding) {
		_context = host.Context with { Events = port };
		_definition = host.Definition;
		_log = host.Log;
		_turnTransitionGate = host.TransitionGate;
		_port = port;
		_spec = spec;
		_binding = binding;
		_ownsBinding = spec.Role != AcpConversationRole.Subagent;
		_retired = host.Retired;
		_terminals = new AcpTerminalManager(_context.Workspace, _log);
		Background = spec.Opening is AdoptedOpening adopted ? adopted.Work : new AcpBackgroundWork(this);
		RestoreContinuation(spec.Seed.Continuation);
		Inherit(spec.Seed);
		_publishedQueueVersion = _pendingSubmissions.Version;
	}

	private AcpAgentDefinition Definition => _definition();

	/// <summary>False once this incarnation failed, was terminalized, retired, or disposed.</summary>
	internal bool Live => !_lifetime.IsCancellationRequested;

	internal bool Attached => _endpoint.IsSet;
	internal bool Retired => _port.Detached;

	internal async Task WaitUntilReadyAsync(CancellationToken ct) {
		if (await _opened.Task.WaitAsync(ct).ConfigureAwait(false) is { } error) throw error;
	}

	internal Task<AgentConversationOutcome> Completion => _completion.Task;

	internal bool Ready {
		get { lock (_gate) return _ready; }
	}

	internal bool Failed {
		get { lock (_gate) return _runtimeFailed; }
	}

	internal long TurnNumber {
		get { lock (_gate) return _turnNumber; }
	}

	internal bool GuidanceSent {
		get { lock (_gate) return _guidanceSent; }
	}

	internal string? ThreadId => SessionId();

	internal IReadOnlyList<AgentTurnSubmission> QueuedSubmissions {
		get { lock (_gate) return _pendingSubmissions.Snapshot(); }
	}

	internal AcpConversationSnapshot Snapshot {
		get { lock (_gate) return new(_ready, [.. _controls.Values], _commands, _turnNumber); }
	}

	internal AgentUsageSnapshot Usage {
		get { lock (_gate) return new(_contextUsage, [.. _usageLimits.Values]); }
	}

	internal AcpConversationState Continuation {
		get {
			lock (_gate) return new() {
				ConversationId = _spec.Seed.Continuation.ConversationId,
				SessionId = SessionId(),
				AnchorTurnNumber = _spec.Seed.Continuation.AnchorTurnNumber,
				InitialPrompt = _spec.Seed.Continuation.InitialPrompt,
				TurnNumber = _turnNumber,
				GuidanceSent = _guidanceSent,
				PlanTurns = new Dictionary<string, string>(_planTurns),
				Failed = _runtimeFailed,
			};
		}
	}

	/// <summary>Whether the conversation is opening, has queued or running work, or waits on the user.</summary>
	internal bool HasWork {
		get {
			lock (_gate) return _sessionOpening || _pendingSubmissions.Count > 0 || _promptActive
				|| _promptDeliveryActive || _steering || HasBackgroundWorkLocked() || HasPendingInteractionLocked();
		}
	}

	internal bool Settled {
		get { lock (_gate) return _ready && !_controlMutationActive && _controlMutations.Count == 0 && !HasWork; }
	}

	/// <summary>Whether a request opening the provider session is still in flight.</summary>
	internal bool Opening {
		get { lock (_gate) return _sessionOpening; }
	}

	/// <summary>Whether a turn, background tool, or user interaction is in progress.</summary>
	internal bool Busy {
		get { lock (_gate) return _promptActive || HasBackgroundWorkLocked() || HasPendingInteractionLocked(); }
	}

	internal void RestoreContinuation(AcpConversationState state) {
		if (Attached) throw new InvalidOperationException("An attached ACP conversation keeps its continuation.");
		lock (_gate) {
			_sessionId = state.SessionId;
			_turnNumber = state.TurnNumber;
			_guidanceSent = state.GuidanceSent;
			_planTurns.Clear();
			foreach (var (id, turn) in state.PlanTurns) _planTurns.Add(id, turn);
		}
	}

	internal void Save(IReadOnlyList<AgentPaneMessage> messages) => _port.Save(Continuation, messages);

	private void SaveContinuation() => Save([]);

	/// <summary>Emits a message owned by this conversation's thread.</summary>
	internal void Publish(AgentPaneMessage message) => Emit(message);

	private void Emit(AgentPaneMessage message) {
		if (message.TurnId is { Length: > 0 } turnId
			&& message.ItemId is { Length: > 0 } itemId
			&& message.Type is "agent-message-delta"
				or "thought-message-delta" or "plan-delta" or "item-started" or "item-completed") {
			lock (_gate) {
				if (!_turnItemIds.TryGetValue(turnId, out var items)) {
					items = new HashSet<string>(StringComparer.Ordinal);
					_turnItemIds.Add(turnId, items);
				}
				items.Add(itemId);
			}
		}
		_port.Emit(Continuation, message);
	}

	internal void Observe(AgentEvent value) {
		lock (_turnTransitionGate) {
			if (value is AgentToolEvent tool) {
				lock (_gate) value = tool with { RequiresUserInput = HasPendingInteractionLocked() };
			}
			var feedback = _port.Observe(value);
			foreach (string message in feedback.Messages) Notice(message);
		}
	}

	/// <summary>Adds a host line to this conversation's transcript.</summary>
	internal void Notice(string text) => Emit(new AgentPaneMessage {
		Type = "notice",
		ProviderId = Definition.Id,
		ThreadId = SessionId(),
		Text = text,
	});

	private string? SessionId() {
		lock (_gate) {
			return _sessionId ?? (Live && _endpoint.IsSet ? _endpoint.Value.SessionId : null);
		}
	}

	internal string TurnId() {
		lock (_gate) {
			return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	internal void Run(Func<Task> action) => _ = Task.Run(async () => {
		try {
			await action().ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			if (ex is IOException or AcpProtocolException) FailRuntime(ex);
			else EmitFailure(ex);
		}
	});

	internal void RunRuntime(Func<Task> action) => _ = Task.Run(async () => {
		try {
			await action().ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			FailRuntime(ex);
		}
	});

	private void FailRuntime(Exception error) {
		lock (_turnTransitionGate) FailRuntimeSerialized(error);
	}

	private void FailRuntimeSerialized(Exception error) {
		if (Live && !_port.Fail(error)) Terminate(error);
	}

	private void SignalSettled() {
		bool terminal;
		lock (_gate) terminal = _runtimeFailed;
		_port.Settled(Continuation, terminal);
	}

	private void EmitFailure(Exception error) {
		lock (_gate) {
			if (_disposed) {
				return;
			}
		}
		_log($"[acp:{Definition.Id}] {error}");
		Emit(new AgentPaneMessage {
			Type = "error",
			ProviderId = Definition.Id,
			ThreadId = SessionId(),
			Summary = $"{Definition.Name} ACP error",
			Text = error.Message,
			Status = "error",
		});
	}

	private sealed record AcpPendingRequest(
		AcpClientRequest Request,
		string Kind,
		JsonElement Data,
		string? ThreadId,
		string TurnId,
		string ItemId) {
		public (string Path, string Text)? Plan { get; init; }
	}

	private sealed record AcpControlMutation(string Axis, string Value);

	private sealed record TerminalizedTool(
		AcpToolState Tool,
		IReadOnlyList<AgentMutation> CompletionMutations);

}
