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
		if (!terminal) return;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (!OwnsSideRuntimeLocked(runtime)) return;
				_sideRuntimes.Remove(runtime.Conversation.ConversationId);
				runtime.Session._endpoint?.Retire();
			}
			PublishSideTerminal(runtime.Conversation);
		}
		DisposeSideRuntime(runtime);
	}

	private void FailSideRuntimes(Exception error) {
		SideRuntime[] sides;
		lock (_gate) sides = [.. _sideRuntimes.Values];
		foreach (var side in sides) {
			lock (_turnTransitionGate) side.Session.FailConversationSerialized(error);
		}
	}

	private bool OwnsSideRuntimeLocked(SideRuntime runtime) =>
		_sideRuntimes.TryGetValue(runtime.Conversation.ConversationId, out var current)
		&& ReferenceEquals(current, runtime);

	private void DisposeSideRuntime(SideRuntime runtime) {
		_context.Events.Observe(new AgentConversationRemoved(runtime.Conversation.ConversationId));
		Run(async () => await runtime.Session.DisposeAsync().ConfigureAwait(false));
	}

	private void PublishSideTerminal(SideConversation conversation) {
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

	private sealed class SideEventSink(AcpAgentSession owner, string conversationId) : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) {
			lock (owner._turnTransitionGate) {
				lock (owner._gate) {
					if (!owner._sideRuntimes.ContainsKey(conversationId)) return AgentEventFeedback.None;
				}
				return owner._context.Events.Observe(new AgentConversationEvent(conversationId, value));
			}
		}
	}

	private sealed record SideRequestOwner(AcpAgentSession Session, string RequestId);
}
