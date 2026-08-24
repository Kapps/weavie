using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Editor;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>One worktree-scoped ACP conversation rendered in Weavie's native pane.</summary>
public sealed partial class AcpAgentSession : IStructuredAgentSession, IStructuredAgentControls, IStructuredAgentUsage {
	private readonly AgentSessionContext _context;
	private readonly AcpAgentDefinition _definition;
	private readonly AcpSessionStore _sessions;
	private readonly AcpControlStore _controlDefaults;
	private readonly Action<string> _log;
	private readonly AcpJsonRpcConnection _connection;
	private readonly WorkspaceFileScope _fileScope;
	private readonly AcpTerminalManager _terminals;
	private readonly Lock _gate = new();
	private readonly Lock _submissionDispatchGate = new();
	private readonly Lock _turnTransitionGate = new();
	private readonly LinkedList<AgentTurnSubmission> _pendingSubmissions = [];
	private readonly Queue<AcpControlMutation> _controlMutations = [];
	private readonly ConcurrentDictionary<string, AcpClientRequestState> _clientRequests = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, AcpPendingRequest> _pendingRequests = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, string> _urlElicitations = new(StringComparer.Ordinal);
	private readonly HashSet<string> _resolvedRequests = new(StringComparer.Ordinal);
	private readonly Dictionary<string, AcpToolState> _tools = new(StringComparer.Ordinal);
	private readonly HashSet<string> _activeTools = new(StringComparer.Ordinal);
	private readonly Dictionary<string, AcpContentState> _content = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _planTurns = new(StringComparer.Ordinal);
	private readonly Dictionary<string, HashSet<string>> _turnItemIds = new(StringComparer.Ordinal);
	private string? _replayContentRole;
	private readonly List<AgentPaneMessage> _loadedMessages = [];
	private readonly Dictionary<string, AgentControlAxis> _controls = new(StringComparer.Ordinal);
	private IReadOnlyList<AgentSlashEntry> _commands = [];
	private IReadOnlyList<AcpAuthMethod> _authMethods = [];
	private string? _sessionId;
	private string? _openingSessionId;
	private long _turnNumber;
	private long _activeGeneration;
	private bool _ready;
	private bool _started;
	private bool _disposed;
	private bool _promptActive;
	private bool _steering;
	private bool _loadingTranscript;
	private bool _sessionOpening;
	private bool _waitingForBackground;
	private bool _authenticationPending;
	private bool _authenticating;
	private bool _authenticationOpensSession;
	private CancellationTokenSource? _authenticationCancellation;
	private string? _authenticationItemId;
	private long _authenticationSequence;
	private bool _supportsLoad;
	private bool _supportsResume;
	private bool _supportsClose;
	private bool _supportsImages;
	private bool _supportsEmbeddedContext;
	private bool _supportsHttpMcp;
	private bool _supportsSteering;
	private bool _guidanceSent;
	private bool _runtimeFailed;
	private bool _cancelRequested;
	private bool _controlMutationActive;
	private bool _configOwnsMode;
	private long _submissionEpoch;
	private AgentContextWindowUsage? _contextUsage;
	private readonly Dictionary<string, AgentUsageLimit> _usageLimits = [];

	/// <summary>Creates a supervised ACP conversation.</summary>
	public AcpAgentSession(
		AgentSessionContext context,
		AcpAgentDefinition definition,
		AcpSessionStore sessions,
		AcpControlStore controlDefaults,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(controlDefaults);
		ArgumentNullException.ThrowIfNull(log);
		_context = context;
		_definition = definition;
		_sessions = sessions;
		_controlDefaults = controlDefaults;
		_log = log;
		_fileScope = new WorkspaceFileScope([context.Workspace]);
		_terminals = new AcpTerminalManager(context.Workspace, _fileScope, log);
		_connection = new AcpJsonRpcConnection(definition, context.Workspace, log);
		_connection.ProcessStarted += OnProcessStarted;
		_connection.ProcessStateChanged += change => Observe(new AgentProcessChanged(change));
		_connection.NotificationReceived += HandleNotification;
		_connection.RequestReceived += RegisterClientRequest;
		_connection.ProtocolFaulted += FailRuntime;
	}

	/// <inheritdoc/>
	public event Action<AgentPaneMessage>? PaneMessage;

	/// <inheritdoc/>
	public event Action<IReadOnlyList<AgentPaneMessage>>? PaneSnapshot;

	/// <inheritdoc/>
	public event Action<AgentControlState>? ControlStateChanged;

	/// <inheritdoc/>
	public event Action<AgentUsageSnapshot>? UsageChanged;

	/// <inheritdoc/>
	public AgentControlState ControlState {
		get {
			lock (_gate) {
				return new AgentControlState {
					Axes = [.. _controls.Values],
					Slash = _commands,
				};
			}
		}
	}

	/// <inheritdoc/>
	public AgentUsageSnapshot Snapshot {
		get { lock (_gate) return new(_contextUsage, [.. _usageLimits.Values]); }
	}

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
		PaneMessage?.Invoke(message);
	}

	private void Observe(AgentEvent value) {
		var feedback = _context.Events.Observe(value);
		foreach (string message in feedback.Messages) {
			Emit(new AgentPaneMessage {
				Type = "notice",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				Text = message,
			});
		}
	}

	private string? SessionId() {
		lock (_gate) {
			return _sessionId ?? _openingSessionId;
		}
	}

	private string TurnId() {
		lock (_gate) {
			return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	private void Run(Func<Task> action) => _ = Task.Run(async () => {
		try {
			await action().ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			if (ex is IOException or AcpProtocolException) FailRuntime(ex);
			else EmitFailure(ex);
		}
	});

	private void RunRuntime(long generation, Func<Task> action) => _ = Task.Run(async () => {
		try {
			await action().ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			lock (_turnTransitionGate) {
				if (OwnsGeneration(generation)) FailRuntimeSerialized(ex);
			}
		}
	});

	private bool OwnsGeneration(long generation) {
		lock (_gate) return !_disposed && _activeGeneration == generation;
	}

	private void FailRuntime(Exception error) {
		lock (_turnTransitionGate) FailRuntimeSerialized(error);
	}

	private void FailRuntime(long generation, Exception error) {
		lock (_turnTransitionGate) {
			bool owns;
			lock (_gate) {
				owns = !_disposed && !_runtimeFailed && (_activeGeneration == generation
					|| (_activeGeneration == 0 && _connection.IsLatestGeneration(generation)));
				if (owns && _activeGeneration == 0) _activeGeneration = generation;
			}
			if (owns) FailRuntimeSerialized(error);
		}
	}

	private void FailRuntimeSerialized(Exception error) {
		TerminalizedTool[] tools;
		bool promptActive;
		long generation;
		lock (_gate) {
			if (_disposed || _runtimeFailed) return;
			generation = _activeGeneration;
			_activeGeneration = 0;
			_runtimeFailed = true;
			_ready = false;
			promptActive = _promptActive;
			_promptActive = false;
			_steering = false;
			_waitingForBackground = false;
			_cancelRequested = false;
			_controlMutations.Clear();
			_submissionEpoch++;
			tools = TerminalizeActiveToolsLocked("failed");
		}
		if (generation > 0) {
			_terminals.ReleaseGeneration(generation);
			_connection.TerminateGeneration(
				generation,
				string.IsNullOrEmpty(error.Message) ? "ACP runtime failure." : error.Message);
		}
		AbandonClientRequests();
		ObserveTerminalizedTools(tools);
		Observe(new AgentRuntimeFailed());
		CompleteContentStreams();
		PublishTerminalizedToolMessages(tools);
		if (promptActive) {
			Emit(new AgentPaneMessage {
				Type = "turn-completed",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				Status = "failed",
				Summary = error.Message,
			});
		}
		EmitFailure(error);
	}

	private TerminalizedTool[] TerminalizeActiveToolsLocked(string status) {
		var result = new List<TerminalizedTool>();
		foreach (string id in _activeTools.ToArray()) {
			var tool = _tools[id];
			tool.Status = status;
			var completions = PendingMutationCompletions(tool);
			_activeTools.Remove(id);
			result.Add(new TerminalizedTool(tool, completions));
		}
		return [.. result];
	}

	private bool HasBackgroundWorkLocked() => _activeTools.Count > 0;

	private void ObserveTerminalizedTools(IEnumerable<TerminalizedTool> tools) {
		foreach (var terminalized in tools) {
			foreach (var mutation in terminalized.CompletionMutations) {
				Observe(new AgentToolCompleted(mutation));
			}
		}
	}

	private void EnsureObservedMutation(AcpToolState tool) {
		var mutation = Mutation(tool);
		string key = MutationKey(mutation);
		if (!tool.ObservedMutationKeys.Add(key)) return;
		tool.ObservedMutations.Add(mutation);
		Observe(new AgentToolStarting(mutation));
	}

	private static AgentMutation[] PendingMutationCompletions(AcpToolState tool) {
		var result = tool.ObservedMutations.Skip(tool.CompletedMutationCount).ToArray();
		tool.CompletedMutationCount = tool.ObservedMutations.Count;
		return result;
	}

	private static string MutationKey(AgentMutation mutation) => mutation switch {
		AgentMutation.None => "none",
		AgentMutation.File file => $"file:{file.Path}",
		AgentMutation.Files files => $"files:{string.Join('\n', files.Items.Select(file => file.Path))}",
		_ => throw new InvalidOperationException("Unknown agent mutation type."),
	};

	private void PublishTerminalizedToolMessages(IEnumerable<TerminalizedTool> tools) {
		foreach (var terminalized in tools) {
			PublishTool(terminalized.Tool);
		}
	}

	private void EmitFailure(Exception error) {
		lock (_gate) {
			if (_disposed) {
				return;
			}
		}
		_log($"[acp:{_definition.Id}] {error}");
		Emit(new AgentPaneMessage {
			Type = "error",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			Summary = $"{_definition.Name} ACP error",
			Text = error.Message,
			Status = "error",
		});
	}

	private sealed record AcpPendingRequest(
		AcpClientRequest Request,
		string Kind,
		JsonElement Data,
		string? ThreadId,
		string TurnId);

	private sealed class AcpClientRequestState : IDisposable {
		private readonly CancellationTokenSource _cancellation;
		private readonly Lock _gate = new();
		private bool _completed;
		private bool _published;

		public AcpClientRequestState(AcpClientRequest request) {
			Request = request;
			_cancellation = new CancellationTokenSource();
			Token = _cancellation.Token;
		}

		public AcpClientRequest Request { get; }

		public CancellationToken Token { get; }

		public bool TryComplete() => TryComplete(out _);

		public bool TryComplete(out bool published) {
			lock (_gate) {
				published = false;
				if (_completed) return false;
				_completed = true;
				published = _published;
				return true;
			}
		}

		public bool TryCancel() {
			lock (_gate) {
				if (_completed) return false;
				_completed = true;
			}
			_cancellation.Cancel();
			return true;
		}

		public bool PublishDeferred(Action publish) {
			ArgumentNullException.ThrowIfNull(publish);
			lock (_gate) {
				if (_completed) return false;
				publish();
				_published = true;
				return true;
			}
		}

		public void Dispose() => _cancellation.Dispose();
	}

	private sealed record AcpAuthMethod(
		string Id,
		string Name,
		string? Description,
		string Type,
		IReadOnlyList<string> Arguments,
		IReadOnlyDictionary<string, string> Environment);

	private sealed record AcpControlMutation(string Axis, string Value);

	private sealed record TerminalizedTool(
		AcpToolState Tool,
		IReadOnlyList<AgentMutation> CompletionMutations);

	private sealed class AcpToolState {
		public required string Id { get; init; }
		public required string TurnId { get; init; }
		public string? Title { get; set; }
		public string? Kind { get; set; }
		public string? Status { get; set; }
		public string? Text { get; set; }
		public IReadOnlyList<AgentPaneLocation>? Locations { get; set; }
		public IReadOnlyList<AgentPaneDiff>? Diffs { get; set; }
		public IReadOnlyList<AgentPaneContent>? Content { get; set; }
		public string? TerminalId { get; set; }
		public long? StartedAtMs { get; set; }
		public bool MutationMetadataDisclosed { get; set; }
		public List<AgentMutation> ObservedMutations { get; } = [];
		public HashSet<string> ObservedMutationKeys { get; } = new(StringComparer.Ordinal);
		public int CompletedMutationCount { get; set; }
		public bool StartedObserved => ObservedMutations.Count > 0;
		public bool CompletedObserved => CompletedMutationCount == ObservedMutations.Count;
	}

	private sealed class AcpContentState {
		public required string Id { get; init; }
		public required string ItemType { get; init; }
		public required string TurnId { get; init; }
		public StringBuilder Text { get; } = new();
		public string? MediaType { get; set; }
		public string? MediaData { get; set; }
		public string? ResourceUri { get; set; }
	}

}
