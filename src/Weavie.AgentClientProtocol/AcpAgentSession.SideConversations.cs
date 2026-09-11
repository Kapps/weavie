using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private readonly Dictionary<string, SideRuntime> _sideRuntimes = new(StringComparer.Ordinal);
	private readonly Queue<AgentTurnSubmission> _pendingAsides = new();

	/// <inheritdoc/>
	public void AskAside(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		if (submission.Kind != AgentTurnSubmissionKind.Prompt || submission.CommandName.Length != 0) {
			throw new ArgumentException("A side question must be an ordinary prompt.", nameof(submission));
		}
		if (submission.Text.Trim().Length == 0 && submission.Attachments.Count == 0) {
			throw new ArgumentException("Write a side question or attach an image.", nameof(submission));
		}
		lock (_turnTransitionGate) {
			bool deferred;
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_role is not PrimaryRole) {
					throw new InvalidOperationException("A side conversation cannot address another side conversation.");
				}
				// _supportsFork/_supportsLoad are only known once the handshake completes; a BTW asked in that
				// same startup window queues here and is drained by FlushPendingAsides, exactly like the primary
				// composer's own turns already do in _pendingSubmissions, instead of failing outright.
				deferred = !_ready;
				if (deferred) _pendingAsides.Enqueue(submission);
			}
			if (!deferred) StartSideConversation(submission);
		}
	}

	// Callers hold _turnTransitionGate: either AskAside starting immediately, or FlushPendingAsides
	// draining a submission once the primary session became ready.
	private void StartSideConversation(AgentTurnSubmission submission) {
		SideRuntime runtime;
		lock (_gate) {
			EnsureSideConversationSupport();
			var conversation = new SideConversation(Guid.NewGuid().ToString("N"), _turnNumber, submission.Text);
			runtime = CreateSideRuntime(conversation, _guidanceSent, _activeGeneration);
			_sideRuntimes.Add(conversation.ConversationId, runtime);
		}
		runtime.Session.Emit(SideMarker(runtime.Conversation, "forking"));
		try {
			runtime.Session.Start();
			runtime.Session.Submit(submission);
		} catch (Exception error) {
			runtime.Session.FailConversationSerialized(error);
		}
	}

	private void FlushPendingAsides() {
		lock (_turnTransitionGate) {
			while (true) {
				AgentTurnSubmission submission;
				lock (_gate) {
					if (_pendingAsides.Count == 0) return;
					submission = _pendingAsides.Dequeue();
				}
				try {
					StartSideConversation(submission);
				} catch (Exception error) {
					// EnsureSideConversationSupport rejected it once capabilities were known (e.g. the connected
					// agent doesn't support forking); nothing was emitted for it yet, so surface it directly.
					EmitFailure(error);
				}
			}
		}
	}

	/// <inheritdoc/>
	public void ReplyAside(string conversationId, string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(conversationId);
		prompt = RequiredSidePrompt(prompt);
		lock (_turnTransitionGate) {
			SideRuntime runtime;
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				_sideRuntimes.TryGetValue(conversationId, out runtime!);
				var state = _sideConversations.GetValueOrDefault(conversationId);
				if (runtime is null && (state is null || state.Failed || state.SessionId is null)) {
					throw new InvalidOperationException("That side conversation is no longer available.");
				}
				EnsureSideConversationSupport();
				if (runtime is null) {
					runtime = CreateSideRuntime(new(state!.ConversationId, state.AnchorTurnNumber, state.InitialPrompt), state.GuidanceSent, _activeGeneration);
					runtime.Session.RestoreContinuation(state);
					_sideRuntimes.Add(conversationId, runtime);
				}
			}
			runtime.Session.Start();
			runtime.Session.Submit(SideTurn(prompt));
		}
	}

	private void EnsureSideConversationSupport() {
		if (_role is not PrimaryRole) {
			throw new InvalidOperationException("A side conversation cannot address another side conversation.");
		}
		if (!_ready || !_supportsFork || !_supportsLoad) {
			throw new InvalidOperationException(
				$"{_definition.Name} does not support context-preserving side conversations.");
		}
	}

	private static string RequiredSidePrompt(string prompt) {
		ArgumentNullException.ThrowIfNull(prompt);
		prompt = prompt.Trim();
		if (prompt.Length == 0) throw new ArgumentException("A side question cannot be empty.", nameof(prompt));
		return prompt;
	}

	private bool HasWork() {
		lock (_gate) return _sessionOpening || _pendingSubmissions.Count > 0 || _promptActive
			|| HasBackgroundWorkLocked() || HasPendingInteractionLocked();
	}

	private SideRuntime CreateSideRuntime(SideConversation conversation, bool guidanceInherited, long generation) {
		var child = new AcpAgentSession(
			_context with { Events = new SideEventSink(this, conversation.ConversationId) },
			_definitionSource,
			_sessions,
			_controlDefaults,
			_log,
			new SideRole(conversation, guidanceInherited, this, generation));
		var runtime = new SideRuntime(child, conversation);
		child.PaneMessage += message => ForwardSideMessage(runtime, message);
		child.SideTurnSettled += terminal => CompleteSideTurn(runtime, terminal);
		return runtime;
	}

	private static AgentTurnSubmission SideTurn(string prompt) => new() {
		Id = Guid.NewGuid().ToString("N"),
		Text = prompt,
		Kind = AgentTurnSubmissionKind.Prompt,
		CommandName = string.Empty,
		Attachments = [],
	};

	private AgentPaneMessage SideMarker(SideConversation conversation, string status) => new() {
		Type = "side-conversation-started",
		ProviderId = _definition.Id,
		ThreadId = SessionId(),
		ConversationId = conversation.ConversationId,
		AnchorTurnId = conversation.AnchorTurnNumber.ToString(
			System.Globalization.CultureInfo.InvariantCulture),
		IsPrimaryThread = false,
		Text = conversation.InitialPrompt,
		Status = status,
	};

	private abstract record AcpSessionRole;
	private sealed record PrimaryRole : AcpSessionRole;
	private sealed record SideRole(
		SideConversation Conversation, bool GuidanceInherited, AcpAgentSession Owner, long Generation) : AcpSessionRole;
	private sealed record SideConversation(
		string ConversationId,
		long AnchorTurnNumber,
		string InitialPrompt);

	private sealed class SideRuntime(AcpAgentSession session, SideConversation conversation) {
		public SideConversation Conversation { get; } = conversation;
		public AcpAgentSession Session { get; } = session;
	}
}
