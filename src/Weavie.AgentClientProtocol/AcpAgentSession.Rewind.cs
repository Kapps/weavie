using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession : IStructuredAgentRewind {
	/// <inheritdoc/>
	public Task RewindLatestAsync() => RewindBeforeAsync(Primary.TurnId());

	/// <inheritdoc/>
	public async Task RewindBeforeAsync(string turnId) {
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		AcpRewindPlan plan;
		AcpConversation primary;
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			primary = _primary;
			if (!primary.Ready || !Forkable) throw new InvalidOperationException($"{_definition.Name} cannot rewind this conversation now.");
			if (primary.Rewinding || primary.HasWork || Sides().Any(side => side.Conversation.HasWork)) {
				throw new InvalidOperationException("Wait for the agent to finish before rewinding.");
			}
			plan = AcpRewindPlan.Create(_sessions.ReadMessages(_definition.Id, _context.Workspace), turnId);
			primary.Rewinding = true;
		}
		try {
			string? sessionId = plan.ForkMessageId is { } messageId ? await primary.ForkAtAsync(messageId).ConfigureAwait(false) : null;
			lock (_turnTransitionGate) {
				if (!primary.Live) throw new InvalidOperationException("The agent restarted during the rewind.");
				CommitRewind(primary, plan, sessionId);
			}
		} finally {
			primary.Rewinding = false;
			primary.DispatchPendingSubmission();
		}
	}

	private void CommitRewind(AcpConversation predecessor, AcpRewindPlan plan, string? sessionId) {
		var sides = Sides();
		AcpConversationState continuation;
		string[] dropped;
		try {
			predecessor.TerminalizeForRestart(clearSubmissions: false, "Conversation rewound.");
			SuspendSides("Conversation rewound.");
			predecessor.SettleInteractions();
			var state = predecessor.Continuation;
			continuation = state with {
				SessionId = sessionId,
				TurnNumber = plan.Turn - 1,
				GuidanceSent = sessionId is not null && state.GuidanceSent,
				PlanTurns = state.PlanTurns.Where(pair => plan.Keeps(long.Parse(pair.Value, CultureInfo.InvariantCulture)))
					.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
			};
			dropped = [.. _sideConversations.Values.Where(side => !plan.Keeps(side.AnchorTurnNumber)).Select(side => side.ConversationId)];
			_sessions.Replace(_definition.Id, _context.Workspace, [continuation, .. _sideConversations.Values.Where(side => !dropped.Contains(side.ConversationId))], plan.Kept);
		} catch (AcpSessionStoreException error) {
			StopForStorageFailure(error, sides);
			throw;
		}
		foreach (string conversationId in dropped) _sideConversations.Remove(conversationId);
		ReplacePrimary(predecessor.Retire() with { Continuation = continuation });
		PaneSnapshot?.Invoke(plan.Kept);
		_connection.Restart();
		if (plan.Prompt.Length > 0) _primary.Prefill(plan.Prompt);
	}
}
