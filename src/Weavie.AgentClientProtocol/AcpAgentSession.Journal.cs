using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private readonly Dictionary<string, AcpConversationState> _sideConversations = new(StringComparer.Ordinal);
	private volatile bool _storageFailed;
	private Task _archiveStop = Task.CompletedTask;
	private readonly Lock _archiveGate = new();
	private bool _displayRestored;
	private Exception? _restoreFailure;

	private void ArchiveFailed(AcpSessionStoreException error) {
		lock (_archiveGate) {
			if (_storageFailed) return;
			_storageFailed = true;
			_archiveStop = Task.Run(() => {
				_runtime.Fail(error);
			});
		}
	}

	/// <inheritdoc/>
	public void Restore() {
		try {
			Output.Restore(RestoreDisplay());
		} catch (Exception error) {
			// Reported by Start, once the session's failure observers are wired.
			_restoreFailure = error;
		}
	}

	private List<AgentPaneMessage> RestoreDisplay() {
		lock (_turnTransitionGate) {
			try {
				var states = _sessions.ReadConversations(_definition.Id, _context.Workspace);
				var messages = _sessions.ReadMessages(_definition.Id, _context.Workspace);
				_sideConversations.Clear();
				foreach (var state in states) {
					if (state.ConversationId.Length == 0) _primary.RestoreContinuation(Untouched(state));
					else _sideConversations.Add(state.ConversationId, state);
				}
				_storageFailed = false;
				var recovery = AgentPaneRecovery.Interrupt(messages).ToList();
				var ended = messages.Where(message => message.Type is "side-conversation-closed" or "side-conversation-failed")
					.Select(message => message.ConversationId).ToHashSet(StringComparer.Ordinal);
				recovery.AddRange(messages.Where(message => message.Type == "side-conversation-started"
					&& message.ConversationId is { } id && !_sideConversations.ContainsKey(id) && !ended.Contains(id))
					.Select(message => message with { Type = "side-conversation-closed", Text = null, Summary = null, Status = "closed" }));
				if (recovery.Count > 0) _primary.Save(recovery);
				var terminalSides = messages.Where(message => message.Type == "side-conversation-failed")
					.Select(message => message.ConversationId).ToHashSet(StringComparer.Ordinal);
				foreach (var state in _sideConversations.Values.ToArray()) {
					if ((!state.Failed && state.SessionId is not null) || terminalSides.Contains(state.ConversationId)) continue;
					var failed = state with { Failed = true };
					var terminal = SideTerminal(Side(state));
					_sessions.Save(_definition.Id, _context.Workspace, failed, [terminal]);
					_sideConversations[state.ConversationId] = failed;
					recovery.Add(terminal);
				}
				_displayRestored = true;
				return [.. messages, .. recovery];
			} catch (AcpSessionStoreException) {
				_storageFailed = true;
				throw;
			}
		}
	}
}
