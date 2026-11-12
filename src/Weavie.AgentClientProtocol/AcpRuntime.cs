using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>One supervised ACP process, its initialization, and the conversations hosted on it.</summary>
internal sealed class AcpRuntime : IAsyncDisposable {
	private readonly AgentSessionContext _context;
	private readonly Func<AcpAgentDefinition> _definitionSource;
	private readonly AcpJsonRpcConnection _connection;
	private readonly AcpConversationHost _host;
	private readonly Action _backgroundChanged;
	private IReadOnlyList<AgentBackgroundItem> _background = [];
	private readonly HashSet<AcpConversation> _conversations = [];
	private readonly HashSet<Task> _closing = [];
	private AcpConversation? _ingress;
	private long _generation;
	private bool _failing;
	private bool _disposed;
	private Task? _disposal;

	internal AcpRuntime(AgentSessionContext context, Func<AcpAgentDefinition> definition, AcpControlStore controls, Action<string> log, Action backgroundChanged) {
		_context = context;
		ControlDefaults = controls;
		_backgroundChanged = backgroundChanged;
		_definitionSource = definition;
		Definition = definition() ?? throw new InvalidOperationException("The ACP agent definition is unavailable.");
		_connection = new AcpJsonRpcConnection(ResolveDefinition, context.Workspace, log);
		_host = new AcpConversationHost(context, () => Definition, log, TransitionGate, Remove);
		_connection.ProcessStarted += OnProcessStarted;
		_connection.ProcessStateChanged += change => {
			lock (TransitionGate) _ingress?.Observe(new AgentProcessChanged(change));
		};
		_connection.NotificationReceived += (generation, root) => {
			lock (TransitionGate) if (generation == _generation) _ingress?.HandleNotification(root);
		};
		_connection.RequestReceived += request => {
			lock (TransitionGate) {
				if (request.Generation == _generation && _ingress is { } owner) owner.RegisterClientRequest(request);
				else _connection.RejectClosedRequest(request);
			}
		};
		_connection.ProtocolFaulted += (generation, error) => {
			lock (TransitionGate) {
				// Attached conversations receive faults through their endpoints; launch failures have none.
				if (_ingress is { Attached: false } && _connection.IsLatestGeneration(generation)) Fail(error);
			}
		};
	}

	internal Lock TransitionGate { get; } = new();
	internal AcpControlStore ControlDefaults { get; }
	internal IReadOnlyList<AgentBackgroundItem> Background => Volatile.Read(ref _background);
	internal AcpAgentDefinition Definition { get; private set; }
	internal AcpAgentFeatures Features { get; private set; } = AcpAgentFeatures.None;
	internal AcpProcess? Process { get; private set; }

	internal AcpConversation Create(AcpConversationSpec spec, AcpConversationPort port, AgentConversationBinding binding) {
		lock (TransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (spec.Opening is ForkFromOpening fork && (!_conversations.Contains(fork.Parent) || !fork.Parent.Live)) {
				throw new InvalidOperationException("Forking requires a live parent on this ACP runtime.");
			}
			var conversation = new AcpConversation(_host, spec, port, binding);
			_conversations.Add(conversation);
			return conversation;
		}
	}

	internal void Select(AcpConversation conversation) {
		lock (TransitionGate) {
			if (!_conversations.Contains(conversation)) throw new InvalidOperationException("The conversation belongs to another ACP runtime.");
			_ingress = conversation;
		}
	}

	internal void Start() => _connection.Start();

	internal void Restart(AcpConversation successor) {
		lock (TransitionGate) {
			Select(successor);
			foreach (var conversation in _conversations.ToArray()) {
				if (conversation == successor) continue;
				conversation.Terminate(new InvalidOperationException("ACP agent restarted."));
				conversation.Retire();
			}
			(_generation, Process) = (0, null);
			_connection.Restart();
		}
	}

	internal void Open(AcpConversation conversation, AcpProcess process) {
		lock (TransitionGate) {
			if (!_conversations.Contains(conversation)) throw new InvalidOperationException("The conversation belongs to another ACP runtime.");
			conversation.Attach(process);
			conversation.RunRuntime(() => conversation.OpenAsync(Features));
		}
	}

	internal bool Fail(Exception error) => Fail(error, cancel: false);

	internal bool Fail(Exception error, bool cancel) {
		lock (TransitionGate) {
			if (_failing) return true;
			var conversations = _conversations.Where(conversation => !conversation.Failed).ToArray();
			if (conversations.Length == 0) return false;
			_failing = true;
			try {
				Stop(error.Message);
				foreach (var conversation in conversations) conversation.Terminate(error, cancel);
			} finally {
				_failing = false;
			}
			return true;
		}
	}

	internal void Stop(string reason) {
		lock (TransitionGate) if (_generation > 0) _connection.TerminateGeneration(_generation, reason);
	}

	internal AcpConversation[] Conversations {
		get { lock (TransitionGate) return [.. _conversations]; }
	}

	internal Task CloseAsync(AcpConversation conversation) {
		lock (TransitionGate) {
			if (_conversations.Contains(conversation)) {
				if (conversation.Live) conversation.TerminalizeForRestart(clearSubmissions: true, "Conversation closed.");
				conversation.SettleInteractions();
				conversation.Observe(new AgentConversationRemoved(conversation.ConversationId));
				conversation.Forget();
				conversation.Retire();
				conversation.RaiseBackground();
			}
			var closing = conversation.CloseAsync();
			if (_closing.Add(closing)) _ = ObserveCloseAsync(closing);
			return closing;
		}
	}

	private async Task ObserveCloseAsync(Task closing) {
		try {
			await closing.ConfigureAwait(false);
		} catch (Exception error) {
			Fail(error);
		} finally {
			lock (TransitionGate) _closing.Remove(closing);
		}
	}

	private void Remove(AcpConversation conversation) {
		lock (TransitionGate) {
			if (conversation.Retired) _conversations.Remove(conversation);
			var closing = conversation.Retired ? conversation.CloseAsync() : conversation.RevokeToolsAsync();
			if (_closing.Add(closing)) _ = ObserveCloseAsync(closing);
			UpdateBackground();
		}
	}

	internal void UpdateBackground() {
		lock (TransitionGate) Volatile.Write(ref _background, [.. _conversations.Where(conversation => conversation.OwnsWork).SelectMany(conversation => conversation.Background.Items)]);
		_backgroundChanged();
	}

	private AcpAgentDefinition ResolveDefinition() {
		var definition = _definitionSource() ?? throw new InvalidOperationException("The ACP agent definition is unavailable.");
		if (definition.Id != Definition.Id) throw new InvalidOperationException("An ACP agent definition cannot change provider identity.");
		return Definition = definition;
	}

	private void OnProcessStarted(AcpProcessGeneration started) {
		AcpConversation conversation;
		lock (TransitionGate) {
			conversation = _ingress ?? throw new InvalidOperationException("Select a conversation before starting its ACP runtime.");
			(_generation, Process) = (started.Generation, null);
			conversation.Attach(new AcpProcess(_connection, started.Generation));
		}
		conversation.RunRuntime(() => InitializeAsync(conversation, started.Generation));
	}

	private async Task InitializeAsync(AcpConversation conversation, long generation) {
		var features = AcpAgentFeatures.Read(await _connection.RequestAsync(
			"initialize", InitializeParameters(), generation, CancellationToken.None).ConfigureAwait(false));
		lock (TransitionGate) {
			if (!conversation.Live) return;
			(Features, Process) = (features, new AcpProcess(_connection, generation));
		}
		await conversation.OpenAsync(features).ConfigureAwait(false);
	}

	private JsonObject InitializeParameters() {
		var session = AcpTransientConnection.SessionCapabilities();
		session["notices"] = new JsonObject();
		return new JsonObject {
			["protocolVersion"] = 1,
			["clientCapabilities"] = new JsonObject {
				["auth"] = new JsonObject { ["terminal"] = true },
				["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
				["plan"] = new JsonObject(),
				["subagents"] = new JsonObject(),
				["terminal"] = true,
				["session"] = session,
				["elicitation"] = new JsonObject { ["form"] = new JsonObject(), ["url"] = new JsonObject() },
				["_meta"] = new JsonObject {
					["jetbrains"] = new JsonObject {
						["air"] = new JsonObject {
							["version"] = 1,
							["capabilities"] = new JsonArray("nativeSubagentSessions", "asyncTasks", "planFile"),
						},
					},
				},
			},
			["clientInfo"] = AcpTransientConnection.ClientInfo(_context.Runtime.Build.ToString(System.Globalization.CultureInfo.InvariantCulture)),
		};
	}

	public ValueTask DisposeAsync() {
		TaskCompletionSource completion;
		List<Exception> failures = [];
		lock (TransitionGate) {
			if (_disposal is not null) return new(_disposal);
			completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_disposal = completion.Task;
			_disposed = true;
			foreach (var conversation in _conversations.ToArray()) {
				try {
					try {
						if (conversation.Live) conversation.TerminalizeForRestart(clearSubmissions: true, "ACP runtime closed.");
					} finally {
						conversation.SettleForDisposal();
						conversation.Retire();
					}
				} catch (Exception error) { failures.Add(error); }
			}
		}
		_ = DisposeCoreAsync(completion, failures);
		return new(completion.Task);
	}

	private async Task DisposeCoreAsync(TaskCompletionSource completion, List<Exception> failures) {
		try {
			await _connection.DisposeAsync().ConfigureAwait(false);
			Task[] closing;
			lock (TransitionGate) closing = [.. _closing];
			await Task.WhenAll(closing).ConfigureAwait(false);
		} catch (Exception error) {
			failures.Add(error);
		}
		if (failures.Count > 0) completion.SetException(failures);
		else completion.SetResult();
	}
}
