using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void ForwardSideMessage(SideRuntime runtime, AgentPaneMessage message) {
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (!OwnsSideRuntimeLocked(runtime)) return;
			}
			PaneMessage?.Invoke(message);
		}
	}

	private event Action<bool>? SideTurnSettled;

	private void SignalSideTurnSettled() {
		bool terminal;
		lock (_gate) terminal = _role is SideRole && _runtimeFailed;
		if (_role is SideRole) SideTurnSettled?.Invoke(terminal);
	}

	private void CompleteSideTurn(SideRuntime runtime, bool terminal) {
		bool dispose;
		bool dispatch;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (!OwnsSideRuntimeLocked(runtime)) return;
				bool active = runtime.SubmissionDelivered
					&& string.Equals(
						_activeSideConversationId,
						runtime.Conversation.ConversationId,
						StringComparison.Ordinal);
				if (!terminal && !active) return;
				if (active && runtime.Interrupting) {
					runtime.SettlementPending = true;
					runtime.Terminal |= terminal;
					return;
				}
				dispatch = active;
				dispose = active
					? CompleteSideRuntimeLocked(runtime, terminal)
					: RemoveSideRuntimeLocked(runtime);
			}
			if (dispose) PublishSideTerminal(runtime.Conversation);
		}
		if (dispose) DisposeSideRuntime(runtime);
		if (dispatch) DispatchPendingWork();
	}

	private bool CompleteSideRuntimeLocked(SideRuntime runtime, bool terminal) {
		runtime.SubmissionDelivered = false;
		_activeSideConversationId = null;
		if (!terminal) return false;
		return RemoveSideRuntimeLocked(runtime);
	}

	private bool RemoveSideRuntimeLocked(SideRuntime runtime) {
		_sideRuntimes.Remove(runtime.Conversation.ConversationId);
		runtime.Session._endpoint?.Retire();
		return true;
	}

	private void FailSideRuntimes(Exception error) {
		SideRuntime[] sides;
		lock (_gate) sides = [.. _sideRuntimes.Values];
		foreach (var side in sides) {
			lock (_turnTransitionGate) side.Session.FailConversationSerialized(error);
		}
	}

	private void FinishSideInterruption(SideRuntime runtime) {
		bool dispatch = false;
		bool dispose = false;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (!OwnsSideRuntimeLocked(runtime) || !runtime.Interrupting) return;
				runtime.Interrupting = false;
				if (runtime.SettlementPending) {
					dispatch = true;
					dispose = CompleteSideRuntimeLocked(runtime, runtime.Terminal);
				}
			}
			if (dispose) PublishSideTerminal(runtime.Conversation);
		}
		if (dispose) DisposeSideRuntime(runtime);
		if (dispatch) DispatchPendingWork();
	}

	private void FailSideSubmission(
		string conversationId,
		SideConversation? conversation,
		Exception error) {
		bool failed;
		lock (_turnTransitionGate) {
			lock (_gate) {
				failed = string.Equals(_activeSideConversationId, conversationId, StringComparison.Ordinal);
				if (failed) {
					_activeSideConversationId = null;
					if (_sideRuntimes.TryGetValue(conversationId, out var runtime)) {
						runtime.SubmissionDelivered = false;
					}
				}
			}
			if (failed) EmitSideFailure(conversationId, conversation, error);
		}
		if (failed) DispatchPendingWork();
	}

	private bool OwnsSideRuntimeLocked(SideRuntime runtime) =>
		_sideRuntimes.TryGetValue(runtime.Conversation.ConversationId, out var current)
		&& ReferenceEquals(current, runtime);

	private void DisposeSideRuntime(SideRuntime runtime) =>
		Run(async () => await runtime.Session.DisposeAsync().ConfigureAwait(false));

	private void EmitSideFailure(
		string conversationId,
		SideConversation? conversation,
		Exception error) {
		Emit(new AgentPaneMessage {
			Type = "side-conversation-failed",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			ConversationId = conversationId,
			AnchorTurnId = conversation?.AnchorTurnNumber.ToString(
				System.Globalization.CultureInfo.InvariantCulture) ?? TurnId(),
			IsPrimaryThread = false,
			Summary = error.Message,
			Status = "failed",
		});
		_log($"[acp:{_definition.Id}:btw] {error}");
	}

	private void PublishSideTerminal(SideConversation conversation) {
		Observe(new AgentTurnStopped(WillResume: false));
		Emit(new AgentPaneMessage {
			Type = "side-conversation-failed",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			ConversationId = conversation.ConversationId,
			AnchorTurnId = conversation.AnchorTurnNumber.ToString(
				System.Globalization.CultureInfo.InvariantCulture),
			IsPrimaryThread = false,
			Status = "failed",
		});
	}

	private bool TrySideRequest(string requestId, out SideRequestOwner owner) {
		int separator = requestId.IndexOf(':', StringComparison.Ordinal);
		lock (_gate) {
			if (separator > 0 && _sideRuntimes.TryGetValue(requestId[..separator], out var runtime)) {
				owner = new SideRequestOwner(runtime.Session, requestId[(separator + 1)..]);
				return true;
			}
		}
		owner = null!;
		return false;
	}

	private sealed record SideRequestOwner(AcpAgentSession Session, string RequestId);
}
