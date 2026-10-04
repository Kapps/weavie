using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void SignalSideTurnSettled() {
		bool terminal;
		lock (_gate) terminal = _runtimeFailed;
		_port.Settled(terminal);
	}

	private void CompleteSideTurn(string conversationId) {
		SideRuntime? runtime;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (!_sideRuntimes.Remove(conversationId, out runtime)) return;
				runtime.Session._endpoint?.Retire();
			}
			runtime.Session._port.Detach();
			PublishSideTerminal(runtime.Conversation);
		}
		DisposeSideRuntime(runtime);
	}

	private void SuspendSideRuntimes(string reason) {
		SideRuntime[] sides;
		lock (_gate) sides = [.. _sideRuntimes.Values];
		foreach (var side in sides) {
			side.Session.TerminalizeForRestart(clearSubmissions: true, reason);
			side.Session.SaveContinuation();
			lock (_gate) _sideRuntimes.Remove(side.Conversation.ConversationId);
			side.Session._port.Detach();
			if (side.Session.SessionId() is null) PublishSideTerminal(side.Conversation);
			DisposeSideRuntime(side);
		}
	}

	private void FailSideRuntimes(Exception error) {
		SideRuntime[] sides;
		lock (_gate) sides = [.. _sideRuntimes.Values];
		foreach (var side in sides) {
			lock (_turnTransitionGate) side.Session.FailConversationSerialized(error);
		}
	}

	private void DisposeSideRuntime(SideRuntime runtime) {
		_events.Observe(new AgentConversationRemoved(runtime.Conversation.ConversationId));
		Run(async () => await runtime.Session.DisposeAsync().ConfigureAwait(false));
	}

	private void PublishSideTerminal(SideConversation conversation) => Emit(SideTerminal(conversation));

	private AgentPaneMessage SideTerminal(SideConversation conversation) => new() {
		Type = "side-conversation-failed",
		ProviderId = _definition.Id,
		ThreadId = SessionId(),
		ConversationId = conversation.ConversationId,
		AnchorTurnId = conversation.AnchorTurnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
		IsPrimaryThread = false,
		Status = "failed",
	};

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
