using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private readonly Dictionary<string, AcpConversationState> _sideConversations = new(StringComparer.Ordinal);
	private bool _storageFailed;
	private bool _displayRestored;

	private AcpConversationState ContinuationState() {
		lock (_gate) return new() {
			ConversationId = _role is SideRole side ? side.Conversation.ConversationId : string.Empty,
			SessionId = SessionId(),
			AnchorTurnNumber = _role is SideRole anchored ? anchored.Conversation.AnchorTurnNumber : 0,
			InitialPrompt = _role is SideRole question ? question.Conversation.InitialPrompt : string.Empty,
			TurnNumber = _turnNumber,
			GuidanceSent = _guidanceSent,
			PlanTurns = new Dictionary<string, string>(_planTurns),
			Failed = _role is SideRole && _runtimeFailed,
		};
	}

	private void RestoreContinuation(AcpConversationState state) {
		_sessionId = state.SessionId;
		_turnNumber = state.TurnNumber;
		_guidanceSent = state.GuidanceSent;
		_planTurns.Clear();
		foreach (var (id, turn) in state.PlanTurns) _planTurns.Add(id, turn);
	}

	private void SaveContinuation() => SaveDisplay([]);

	private void PersistDisplay(AgentPaneMessage message) {
		if (message.Type == "transcript-reset") return;
		var owner = _role is SideRole side ? side.Owner : this;
		// The runtime stops on a storage failure; its failure report must still reach the user.
		if (owner._storageFailed) {
			if (!owner._runtimeFailed) throw new AcpSessionStoreException(
				"The ACP conversation cannot continue until storage is available.", new IOException("Conversation storage failed."));
			return;
		}
		SaveDisplay([message]);
	}

	private void SaveDisplay(IReadOnlyList<AgentPaneMessage> messages) {
		lock (_turnTransitionGate) {
			var owner = _role is SideRole side ? side.Owner : this;
			if (_role is SideRole child && (!owner._sideRuntimes.TryGetValue(child.Conversation.ConversationId, out var runtime)
				|| !ReferenceEquals(runtime.Session, this))) return;
			try {
				var state = ContinuationState();
				_sessions.Save(_definition.Id, _context.Workspace, state, messages);
				if (_role is SideRole) owner._sideConversations[state.ConversationId] = state;
			} catch (AcpSessionStoreException) {
				owner._storageFailed = true;
				throw;
			}
		}
	}

	private void RestoreDisplay() {
		lock (_turnTransitionGate) {
			try {
				var states = _sessions.ReadConversations(_definition.Id, _context.Workspace);
				var messages = _sessions.ReadMessages(_definition.Id, _context.Workspace);
				_sideConversations.Clear();
				foreach (var state in states) {
					if (state.ConversationId.Length == 0) RestoreContinuation(state);
					else _sideConversations.Add(state.ConversationId, state);
				}
				_storageFailed = false;
				var recovery = AgentPaneRecovery.Interrupt(messages).ToList();
				if (recovery.Count > 0) SaveDisplay(recovery);
				var terminalSides = messages.Where(message => message.Type == "side-conversation-failed")
					.Select(message => message.ConversationId).ToHashSet(StringComparer.Ordinal);
				foreach (var state in _sideConversations.Values.ToArray()) {
					if ((!state.Failed && state.SessionId is not null) || terminalSides.Contains(state.ConversationId)) continue;
					var failed = state with { Failed = true };
					var terminal = SideTerminal(new(state.ConversationId, state.AnchorTurnNumber, state.InitialPrompt));
					_sessions.Save(_definition.Id, _context.Workspace, failed, [terminal]);
					_sideConversations[state.ConversationId] = failed;
					recovery.Add(terminal);
				}
				PaneSnapshot?.Invoke([.. messages, .. recovery]);
				_displayRestored = true;
			} catch (AcpSessionStoreException) {
				_storageFailed = true;
				throw;
			}
		}
	}
}
