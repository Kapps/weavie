using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;
using Weavie.Core.Json;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>One native Codex app-server thread, driven over JSONL stdio.</summary>
public sealed partial class CodexAppServerSession : IStructuredAgentSession {
	private readonly AgentSessionContext _context;
	private readonly CodexAppServerClient _client;
	private readonly ICodexHookIntegration _hooks;
	private readonly CodexThreadStore _threads;
	private readonly ConcurrentDictionary<string, CodexServerRequest> _pendingRequests = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();
	private readonly Queue<CodexTurnInput> _pendingInputs = new();
	private readonly List<string> _pendingImages = [];
	private long _nextId;
	private string? _threadId;
	private string? _turnId;
	private bool _started;
	private bool _threadPersisted;

	/// <summary>Creates a worktree-scoped Codex app-server session.</summary>
	public CodexAppServerSession(AgentSessionContext context, CodexThreadStore threads, string command) {
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentException.ThrowIfNullOrEmpty(command);
		_context = context;
		_threads = threads;
		_hooks = new CodexHookIntegration(context.Registry.Port, context.Events, LogClient);
		_client = Client(command, _hooks);
		WireClient();
	}

	internal CodexAppServerSession(
		AgentSessionContext context,
		CodexThreadStore threads,
		string command,
		ICodexHookIntegration hooks) {
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentException.ThrowIfNullOrEmpty(command);
		ArgumentNullException.ThrowIfNull(hooks);
		_context = context;
		_threads = threads;
		_hooks = hooks;
		_client = Client(command, hooks);
		WireClient();
	}

	private CodexAppServerClient Client(string command, ICodexHookIntegration hooks) =>
		new(
			command,
			_context.Workspace,
			hooks.GlobalArguments,
			CodexAppServerConfig.Arguments(_context),
			hooks.AppServerArguments,
			hooks.Environment,
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
		if (CodexAppServerProtocol.TryAdaptNotification(root.GetRawText(), out var agentEvent)) {
			EmitFeedback(_context.Events.Observe(agentEvent));
		}

		string method = root.GetStringOrEmpty("method");
		if (method == "turn/started") {
			RememberTurn(root);
		} else if (method is "turn/completed" or "turn/interrupted") {
			ForgetTurn();
		}

		var paneMessage = CodexPaneMessages.FromNotification(method, CurrentThreadId(), root);
		if (paneMessage is not null) {
			Emit(paneMessage);
		}
	}

	private void HandleRequest(CodexServerRequest request) {
		if (CodexApprovalResponses.CanResolve(request.Method) || CodexInputResponses.CanResolve(request.Method)) {
			_pendingRequests[request.Id] = request;
			_context.Events.Observe(new AgentPermissionRequested());
			Emit(CodexPaneMessages.FromRequest(request));
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
		string turnId = root.GetProperty("params").GetProperty("turn").GetStringOrEmpty("id");
		if (turnId.Length > 0) {
			lock (_gate) {
				_turnId = turnId;
			}
			PersistThread();
		}
	}

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

	private void ForgetTurn() {
		lock (_gate) {
			_turnId = null;
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

	private string? CurrentThreadId() {
		lock (_gate) {
			return _threadId;
		}
	}

	private long NextRequest() => Interlocked.Increment(ref _nextId);

	private string Model() => _context.Settings.RequireString("codex.model");

	private string Sandbox() => _context.Settings.RequireString("codex.sandbox");

	private string ApprovalPolicy() => _context.Settings.RequireString("codex.approvalPolicy");

	private string DeveloperInstructions() => EmbeddedAgentGuidance.Compose(_context.Runtime);

	private void LogClient(string text) {
		if (CodexStderrMessages.TryFromLine(text, CurrentThreadId(), out var message)) {
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

	private void Emit(AgentPaneMessage message) => PaneMessage?.Invoke(message);

	private sealed record CodexTurnInput(string Text, IReadOnlyList<string> Images);
}
