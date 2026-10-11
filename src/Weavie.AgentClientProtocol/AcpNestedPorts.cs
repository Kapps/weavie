using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	// A nested conversation renders inside the primary transcript under its own identity and namespaced request ids.
	private abstract class NestedPort(AcpAgentSession owner, string conversationId, long anchorTurnNumber) : OwnedPort(owner) {
		protected string ConversationId => conversationId;

		protected override AgentEventFeedback OnObserve(AgentEvent value) => value switch {
			AgentProcessChanged or AgentSessionStarted or AgentRuntimeFailed => AgentEventFeedback.None,
			AgentConversationRemoved => Owner._context.Events.Observe(value),
			_ => Owner._context.Events.Observe(new AgentConversationEvent(conversationId, value)),
		};

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) {
			if (message.Type is "transcript-reset" or "draft") return null;
			string? original = message.RequestId;
			string? requestId = original is { Length: > 0 } ? conversationId + ":" + original : null;
			string? itemId = message.ItemId;
			if (requestId is not null) {
				itemId = itemId == original ? requestId : itemId == "request:" + original ? "request:" + requestId : itemId;
			}
			return message with {
				ConversationId = conversationId,
				AnchorTurnId = anchorTurnNumber.ToString(CultureInfo.InvariantCulture),
				IsPrimaryThread = false,
				RequestId = requestId,
				ItemId = itemId,
			};
		}

		protected override void OnControlsChanged() { }

		protected override void OnUsageChanged(AgentUsageSnapshot snapshot) { }

		protected override void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue) { }

		protected override bool OnFail(Exception error) => error is not AcpRequestException && Owner.FailProcess(error);
	}

	private sealed class SidePort(AcpAgentSession owner, SideConversation conversation)
		: NestedPort(owner, conversation.ConversationId, conversation.AnchorTurnNumber) {
		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
			Owner._sessions.Save(Owner._definition.Id, Owner._context.Workspace, state, messages);
			Owner._sideConversations[state.ConversationId] = state;
		}

		protected override void OnSettled(bool terminal) {
			if (terminal) Owner.CompleteSideTurn(ConversationId);
		}
	}

	// A subagent persists only its display; it has no continuation to resume.
	private sealed class SubagentPort(AcpAgentSession owner, AcpConversationSpec spec)
		: NestedPort(owner, spec.Seed.Continuation.ConversationId, spec.Seed.Continuation.AnchorTurnNumber) {
		private readonly AcpBackgroundWork _work = ((AdoptedOpening)spec.Opening).Work;

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) => message.Type == "session-info"
			? null
			: base.Prepare(message.Type == "turn-completed"
				? message with { CompletedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
				: message);

		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) =>
			Owner._sessions.Append(Owner._definition.Id, Owner._context.Workspace, messages);

		protected override void OnSettled(bool terminal) {
			if (terminal) _work.Failed(ConversationId);
		}
	}
}
