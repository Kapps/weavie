using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private readonly Dictionary<string, SideRuntime> _sideRuntimes = new(StringComparer.Ordinal);

	/// <inheritdoc/>
	public void AskAside(string prompt) {
		prompt = RequiredSidePrompt(prompt);
		lock (_turnTransitionGate) {
			SideRuntime runtime;
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				EnsureSideConversationSupport();
				var conversation = new SideConversation(Guid.NewGuid().ToString("N"), _turnNumber, prompt);
				runtime = CreateSideRuntime(conversation, _guidanceSent, _activeGeneration);
				_sideRuntimes.Add(conversation.ConversationId, runtime);
			}
			Emit(SideMarker(runtime.Conversation, "forking"));
			try {
				runtime.Session.Start();
				runtime.Session.Submit(SideTurn(prompt));
			} catch (Exception error) {
				runtime.Session.FailConversationSerialized(error);
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
				runtime = _sideRuntimes.GetValueOrDefault(conversationId)
					?? throw new InvalidOperationException("That side conversation is no longer available.");
				EnsureSideConversationSupport();
			}
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
		string InitialPrompt) {
		public long LocalTurnNumber { get; set; }
	}

	private sealed class SideRuntime(AcpAgentSession session, SideConversation conversation) {
		public SideConversation Conversation { get; } = conversation;
		public AcpAgentSession Session { get; } = session;
	}
}
