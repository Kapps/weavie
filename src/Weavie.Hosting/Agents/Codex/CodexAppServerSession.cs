using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;
using Weavie.Core.Configuration;
using Weavie.Core.Json;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>One native Codex app-server thread, driven over JSONL stdio.</summary>
public sealed partial class CodexAppServerSession : IStructuredAgentSession {
	private readonly AgentSessionContext _context;
	private readonly CodexAppServerClient _client;
	private readonly CodexThreadStore _threads;
	private readonly ConcurrentDictionary<string, CodexServerRequest> _pendingRequests = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();
	private readonly Queue<CodexTurnInput> _pendingInputs = new();
	private readonly HashSet<string> _pendingThreadStarts = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _fileChangeSummaries = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, byte> _resolvedRequests = new(StringComparer.Ordinal);
	private long _nextId;
	private string? _threadId;
	private string? _turnId;
	private bool _turnStarting;
	private bool _started;
	private bool _threadPersisted;
	private bool _awaitingThreadAdoption;

	/// <summary>Creates a worktree-scoped Codex app-server session.</summary>
	public CodexAppServerSession(AgentSessionContext context, CodexThreadStore threads, string command) {
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentException.ThrowIfNullOrEmpty(command);
		_context = context;
		_threads = threads;
		_client = Client(CodexAppServerLaunch.Raw(command, context.Workspace));
		WireClient();
	}

	/// <summary>Creates a worktree-scoped Codex app-server session from a resolved Codex launch.</summary>
	internal CodexAppServerSession(AgentSessionContext context, CodexThreadStore threads, CodexAppServerLaunch launch) {
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentNullException.ThrowIfNull(launch);
		_context = context;
		_threads = threads;
		_client = Client(launch);
		WireClient();
	}

	private CodexAppServerClient Client(CodexAppServerLaunch launch) =>
		new(
			launch,
			[],
			CodexAppServerConfig.Arguments(_context),
			[],
			new Dictionary<string, string>(StringComparer.Ordinal),
			LogClient);

	private void WireClient() {
		_client.ProcessStarted += OnClientStarted;
		_client.ProcessStateChanged += OnProcessStateChanged;
		_client.NotificationReceived += HandleNotification;
		_client.RequestReceived += HandleRequest;
	}

	/// <inheritdoc/>
	public event Action<AgentPaneMessage>? PaneMessage;

	private void HandleNotification(JsonElement root) {
		// Track the turn boundary before anything that can throw, so the active-turn id can never silently
		// desync from Codex and leave a later steer targeting a turn the server has already moved past.
		string method = root.GetStringOrEmpty("method");
		if (method == "serverRequest/resolved") {
			HandleServerRequestResolved(root);
			return;
		}

		bool deferredThreadStart = method == "thread/started" && DeferThreadStart(root);
		bool primary = !deferredThreadStart && IsPrimaryThread(root);
		if (primary && method == "turn/started") {
			RememberTurn(root);
		} else if (primary && method is "turn/completed" or "turn/interrupted") {
			ForgetTurn(root);
		} else if (method == "skills/changed") {
			Run(RefreshSkillsAndPublishAsync);
		}

		bool lifecycle = method is "thread/started" or "turn/started" or "turn/completed" or "turn/interrupted";
		if (!deferredThreadStart && (!lifecycle || primary)
			&& CodexAppServerProtocol.TryAdaptNotification(root.GetRawText(), out var agentEvent)) {
			try {
				EmitFeedback(_context.Events.Observe(agentEvent));
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Emit(new AgentPaneMessage {
					Type = "error",
					ProviderId = "codex",
					ThreadId = CurrentThreadId(),
					Summary = "Change tracking failed for this file",
					Text = ex.Message,
					Status = "error",
				});
			}
		}

		var paneMessage = CodexPaneMessages.FromNotification(method, CurrentThreadId(), root);
		if (paneMessage is not null) {
			string? paneItemKey = AgentPaneIdentity.ItemKey(paneMessage);
			// The fileChange approval request carries only the item id — the changed paths live on the item
			// events, so remember each edit item's summary to give a later approval card its substance.
			// "fileChange" is the mapper's no-changes placeholder, not substance.
			if (paneItemKey is not null
				&& paneMessage is { Category: "edit", Summary.Length: > 0 }
				&& paneMessage.Summary != "fileChange") {
				lock (_gate) {
					_fileChangeSummaries[paneItemKey] = paneMessage.Summary;
				}
			}

			Emit(paneMessage);
		}
	}

	private void HandleRequest(CodexServerRequest request) {
		if (CodexApprovalResponses.IsPermissionApproval(request.Method) && BypassPermissions()) {
			try {
				_client.Respond(request.ResponseId, CodexApprovalResponses.Build(request, "accept"));
			} catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) {
				Emit(new AgentPaneMessage {
					Type = "error",
					ProviderId = "codex",
					ThreadId = CurrentThreadId(),
					ItemId = request.Id,
					ItemType = request.Method,
					Text = $"Codex asked for '{request.Method}' approval, but the bypass auto-accept could not be sent: {ex.Message}",
					Status = "error",
					PayloadJson = request.Message.GetRawText(),
				});
			}

			return;
		}

		if (CodexApprovalResponses.CanResolve(request.Method) || CodexInputResponses.CanResolve(request.Method)) {
			_pendingRequests[request.Id] = request;
			_context.Events.Observe(new AgentPermissionRequested());
			// Past the bypass auto-accept, this request always needs the user — so, like Claude's pass-through gate,
			// resolve it as needing input to drive the session to NeedsInput (and ping a backgrounded pane).
			_context.Events.Observe(new AgentPermissionResolved(RequiresUserInput: true));
			Emit(WithFileChangeSubstance(request, CodexPaneMessages.FromRequest(request)));
			return;
		}

		string message = $"Codex app-server request '{request.Method}' is not supported by this Weavie build.";
		try {
			_client.RespondError(request.ResponseId, -32601, message);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) {
			message += " " + ex.Message;
		}

		Emit(new AgentPaneMessage {
			Type = "error",
			ProviderId = "codex",
			ThreadId = CurrentThreadId(),
			ItemId = request.Id,
			ItemType = request.Method,
			Text = message,
			Status = "error",
			PayloadJson = request.Message.GetRawText(),
		});
	}

	private void RememberTurn(JsonElement root) {
		string turnId = ReadTurnId(root);
		if (turnId.Length > 0) {
			lock (_gate) {
				_turnId = turnId;
				_turnStarting = false;
			}
			PersistThread();
			FlushPendingInputs();
		}
	}

	private static string ReadTurnId(JsonElement root) =>
		root.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("turn", out var turn)
			? turn.GetStringOrEmpty("id")
			: string.Empty;

	private void PersistThread() {
		string? threadId;
		lock (_gate) {
			if (_threadPersisted || string.IsNullOrEmpty(_threadId)) {
				return;
			}

			threadId = _threadId;
			_threadPersisted = true;
		}

		_threads.Adopt(_context.Workspace, threadId);
	}

	// Clear the active turn only when the completion is for the turn we track: a late completion of an older
	// turn must not wipe a newer one Codex has already started.
	private bool ForgetTurn(JsonElement root) {
		string turnId = ReadTurnId(root);
		lock (_gate) {
			if (turnId.Length == 0 || string.Equals(turnId, _turnId, StringComparison.Ordinal)) {
				_turnId = null;
				_turnStarting = false;
				// Approvals are turn-scoped; a late completion of an OLDER turn must not wipe the live
				// turn's harvested edit summaries, so the clear shares the turn-id guard.
				_fileChangeSummaries.Clear();
				return true;
			}
		}
		return false;
	}

	// Upstream fileChange approval params carry no changed paths (only threadId/turnId/itemId/reason/grantRoot);
	// the card's substance is joined from the summaries harvested off that item's own notifications.
	private AgentPaneMessage WithFileChangeSubstance(CodexServerRequest request, AgentPaneMessage card) {
		if (card.Text is not null
			|| !string.Equals(request.Method, "item/fileChange/requestApproval", StringComparison.Ordinal)
			|| !request.Message.TryGetProperty("params", out var parameters)) {
			return card;
		}

		string? itemKey = AgentPaneIdentity.ItemKey(
			parameters.GetStringOrEmpty("threadId"),
			parameters.GetStringOrEmpty("turnId"),
			parameters.GetStringOrEmpty("itemId"));
		lock (_gate) {
			return itemKey is not null && _fileChangeSummaries.TryGetValue(itemKey, out string? changes)
				? card with { Text = changes }
				: card;
		}
	}

	private void FlushPendingInputs() {
		while (true) {
			CodexTurnInput input;
			lock (_gate) {
				if (_pendingInputs.Count == 0) {
					return;
				}

				input = _pendingInputs.Dequeue();
			}

			SubmitTurn(input);
		}
	}

	private (string? ThreadId, string? TurnId) CurrentTurn() {
		lock (_gate) {
			return (_threadId, _turnId);
		}
	}

	private string? CurrentTurnId() {
		lock (_gate) {
			return _turnId;
		}
	}

	private string? CurrentThreadId() {
		lock (_gate) {
			return _threadId;
		}
	}

	private bool IsPrimaryThread(JsonElement root) => IsPrimaryThread(ReadNotificationThreadId(root));

	private bool DeferThreadStart(JsonElement root) {
		string threadId = ReadNotificationThreadId(root);
		lock (_gate) {
			if (!_awaitingThreadAdoption || _threadId is not null || threadId.Length == 0) {
				return false;
			}

			_pendingThreadStarts.Add(threadId);
			return true;
		}
	}

	private static string ReadNotificationThreadId(JsonElement root) {
		if (!root.TryGetProperty("params", out var parameters)) {
			return string.Empty;
		}

		return root.GetStringOrEmpty("method") == "thread/started"
			&& parameters.TryGetProperty("thread", out var thread)
			? thread.GetStringOrEmpty("id")
			: parameters.GetStringOrEmpty("threadId");
	}

	private void HandleServerRequestResolved(JsonElement root) {
		if (!root.TryGetProperty("params", out var parameters)
			|| !parameters.TryGetProperty("requestId", out var id)
			|| !_pendingRequests.TryRemove(CodexAppServerClient.ReadRequestId(id), out var request)) {
			return;
		}

		_resolvedRequests[request.Id] = 0;
		_context.Events.Observe(new AgentPermissionResolved(HasPendingUserRequest()));
		string type = CodexInputResponses.CanResolve(request.Method) ? "input-resolved" : "approval-resolved";
		Emit(ResolvedRequest(request, type, "resolved"));
	}

	private bool IsPrimaryThread(string? threadId) =>
		string.IsNullOrEmpty(threadId) || string.Equals(threadId, CurrentThreadId(), StringComparison.Ordinal);

	private long NextRequest() => Interlocked.Increment(ref _nextId);

	private string Model() => _context.Settings.RequireString(CodexSettings.Model);

	private string Effort() => _context.Settings.RequireString(CodexSettings.Effort);

	private string ServiceTier() => _context.Settings.RequireString(CodexSettings.ServiceTier);

	private bool BypassPermissions() => _context.Settings.GetBool("claude.allowAllTools", fallback: false);

	private string Sandbox() => _context.Settings.RequireString(CodexSettings.Sandbox);

	private string ApprovalPolicy() => _context.Settings.RequireString(CodexSettings.ApprovalPolicy);

	private string DeveloperInstructions() => EmbeddedAgentGuidance.Compose(_context.Runtime);

	private void LogClient(string text) {
		if (CodexUnavailableMessages.TryLaunchFailure(
			text,
			CurrentThreadId(),
			_context.Settings.GetString("codex.path"),
			_context.Settings.FilePath,
			out var message)
			|| CodexStderrMessages.TryFromLine(text, CurrentThreadId(), out message)) {
			Emit(message);
		}
	}

	private void Run(Func<Task> action) => CodexSessionTasks.Run(action, Emit);

	private void EmitFeedback(AgentEventFeedback feedback) {
		foreach (string message in feedback.Messages) {
			Emit(new AgentPaneMessage {
				Type = "edit-location",
				ProviderId = "codex",
				ThreadId = CurrentThreadId(),
				Text = message,
				Summary = "Review edit",
				Status = "ready",
			});
		}
	}

	private void Emit(AgentPaneMessage message) =>
		PaneMessage?.Invoke(message with { IsPrimaryThread = IsPrimaryThread(message.ThreadId) });

	private sealed record CodexTurnInput(string Text, IReadOnlyList<AgentInputAttachment> Images, IReadOnlyList<string> SkillNames);
}
