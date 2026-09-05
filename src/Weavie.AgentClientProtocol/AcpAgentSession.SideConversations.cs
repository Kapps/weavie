using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private readonly Queue<SideSubmission> _pendingSideSubmissions = [];
	private readonly Dictionary<string, SideRuntime> _sideRuntimes = new(StringComparer.Ordinal);
	private string? _activeSideConversationId;

	/// <inheritdoc/>
	public void AskAside(string prompt) {
		prompt = RequiredSidePrompt(prompt);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			EnsureSideConversationSupport();
			_pendingSideSubmissions.Enqueue(new SideSubmission(
				Guid.NewGuid().ToString("N"),
				prompt,
				Create: true));
		}
		DispatchPendingWork();
	}

	/// <inheritdoc/>
	public void ReplyAside(string conversationId, string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(conversationId);
		prompt = RequiredSidePrompt(prompt);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!_sideRuntimes.ContainsKey(conversationId)) {
				throw new InvalidOperationException("That side conversation is no longer available.");
			}
			EnsureSideConversationSupport();
			_pendingSideSubmissions.Enqueue(new SideSubmission(conversationId, prompt, Create: false));
		}
		DispatchPendingWork();
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

	private void DispatchPendingWork() {
		DispatchPendingSubmission();
		DispatchPendingSideSubmission();
	}

	private void DispatchPendingSideSubmission() {
		SideSubmission? submission = null;
		lock (_gate) {
			if (_role is not PrimaryRole
				|| !_ready
				|| _promptActive
				|| _pendingSubmissions.Count > 0
				|| _activeSideConversationId is not null
				|| _pendingSideSubmissions.Count == 0) {
				return;
			}
			submission = _pendingSideSubmissions.Dequeue();
			_activeSideConversationId = submission.ConversationId;
		}
		Run(() => DeliverSideSubmissionAsync(submission));
	}

	private async Task DeliverSideSubmissionAsync(SideSubmission submission) {
		SideConversation? conversation = null;
		AcpSessionEndpoint? unownedEndpoint = null;
		try {
			SideRuntime runtime;
			if (submission.Create) {
				string parentSessionId;
				long generation;
				bool guidanceInherited;
				lock (_gate) {
					parentSessionId = _sessionId
						?? throw new InvalidOperationException("The primary ACP session is not ready.");
					generation = _activeGeneration;
					guidanceInherited = _guidanceSent;
					conversation = new SideConversation(
						submission.ConversationId,
						ProviderSessionId: string.Empty,
						_turnNumber,
						submission.Prompt);
				}
				Emit(SideMarker(conversation, "forking"));
				var endpoint = await Endpoint(generation).ForkAsync(
					new {
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					},
					CancellationToken.None).ConfigureAwait(false);
				unownedEndpoint = endpoint;
				conversation = conversation with {
					ProviderSessionId = endpoint.SessionId,
				};
				lock (_turnTransitionGate) {
					lock (_gate) {
						if (_disposed
							|| _activeGeneration != generation
							|| !string.Equals(_sessionId, parentSessionId, StringComparison.Ordinal)
							|| !string.Equals(_activeSideConversationId, submission.ConversationId, StringComparison.Ordinal)) {
							throw new InvalidOperationException("The primary conversation changed while its side conversation was forking.");
						}
						runtime = CreateSideRuntime(conversation, guidanceInherited, endpoint);
						_sideRuntimes.Add(conversation.ConversationId, runtime);
						unownedEndpoint = null;
						runtime.SubmissionDelivered = true;
					}
					runtime.Session.Start();
					runtime.Session.Submit(SideTurn(submission.Prompt));
				}
			} else {
				lock (_turnTransitionGate) {
					lock (_gate) {
						runtime = _sideRuntimes.GetValueOrDefault(submission.ConversationId)
							?? throw new InvalidOperationException("That side conversation is no longer available.");
						conversation = runtime.Conversation;
						runtime.SubmissionDelivered = true;
					}
					runtime.Session.Submit(SideTurn(submission.Prompt));
				}
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			unownedEndpoint?.Retire();
			FailSideSubmission(submission.ConversationId, conversation, ex);
			if (unownedEndpoint is not null && OwnsGeneration(unownedEndpoint.Generation) && _supportsClose) {
				try {
					await unownedEndpoint.CloseAsync().ConfigureAwait(false);
				} catch (Exception closeError) {
					FailRuntime(unownedEndpoint.Generation, closeError);
				}
			}
		}
	}

	private SideRuntime CreateSideRuntime(SideConversation conversation, bool guidanceInherited, AcpSessionEndpoint endpoint) {
		var child = new AcpAgentSession(
			_context,
			_definitionSource,
			_sessions,
			_controlDefaults,
			_log,
			new SideRole(conversation, guidanceInherited, this, endpoint));
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
		SideConversation Conversation, bool GuidanceInherited, AcpAgentSession Owner, AcpSessionEndpoint Endpoint) : AcpSessionRole {
		public long Generation => Endpoint.Generation;
	}
	private sealed record SideSubmission(string ConversationId, string Prompt, bool Create);
	private sealed record SideConversation(
		string ConversationId,
		string ProviderSessionId,
		long AnchorTurnNumber,
		string InitialPrompt) {
		public long LocalTurnNumber { get; set; }
	}

	private sealed class SideRuntime(AcpAgentSession session, SideConversation conversation) {
		public SideConversation Conversation { get; } = conversation;
		public AcpAgentSession Session { get; } = session;
		public bool Interrupting { get; set; }
		public bool SettlementPending { get; set; }
		public bool SubmissionDelivered { get; set; }
		public bool Terminal { get; set; }
	}
}
