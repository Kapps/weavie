using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	// A nested conversation renders inside the primary transcript under its own identity and namespaced request ids.
	private abstract class NestedPort(AcpAgentSession owner, AcpRuntime runtime, string conversationId, long anchorTurnNumber) : OwnedPort(owner, runtime, conversationId) {
		protected string ConversationId { get; } = conversationId;

		protected override AgentEventFeedback OnObserve(AgentEvent value) => value switch {
			AgentProcessChanged or AgentSessionStarted or AgentRuntimeFailed => AgentEventFeedback.None,
			AgentConversationRemoved => Owner._context.Events.Observe(value),
			_ => Owner._context.Events.Observe(new AgentConversationEvent(ConversationId, value)),
		};

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) {
			if (message.Type is "transcript-reset" or "draft") return null;
			string? original = message.RequestId;
			string? requestId = original is { Length: > 0 } ? ConversationId + ":" + original : null;
			string? itemId = message.ItemId;
			if (requestId is not null) {
				itemId = itemId == original ? requestId : itemId == "request:" + original ? "request:" + requestId : itemId;
			}
			return message with {
				ConversationId = ConversationId,
				AnchorTurnId = anchorTurnNumber.ToString(CultureInfo.InvariantCulture),
				IsPrimaryThread = false,
				RequestId = requestId,
				ItemId = itemId,
			};
		}

		protected override void OnControlsChanged() { }

		// Only the primary conversation's controls become provider defaults.
		protected override void OnRememberControl(string axis, string value) { }

		protected override void OnForgetControl(string axis, string value) { }

		protected override void OnUsageChanged(AgentUsageSnapshot snapshot) { }

		protected override void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue) { }

		protected override bool OnFail(Exception error) => error is not AcpRequestException && Runtime.Fail(error);
	}

	private sealed class SidePort(AcpAgentSession owner, SideConversation conversation)
		: NestedPort(owner, owner._runtime, conversation.ConversationId, conversation.AnchorTurnNumber) {
		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
			Owner._sessions.Save(Owner._definition.Id, Owner._context.Workspace, state, messages);
			Owner._sideConversations[state.ConversationId] = state;
		}

		protected override void OnSettled(AcpConversationState state, bool terminal) {
			if (terminal) Owner.CompleteSideTurn(ConversationId);
		}

		// A side question runs with the primary's live controls.
		protected override IReadOnlyDictionary<string, string> OnControlDefaults() =>
			Owner.Primary.Snapshot.Axes.ToDictionary(axis => axis.Id, axis => axis.Value, StringComparer.Ordinal);
	}

	// A subagent persists only its display; it has no continuation to resume.
	private sealed class SubagentPort(AcpAgentSession owner, AcpRuntime runtime, AcpConversationSpec spec)
		: NestedPort(owner, runtime, spec.Seed.Continuation.ConversationId, spec.Seed.Continuation.AnchorTurnNumber) {
		private readonly AcpBackgroundWork _work = ((AdoptedOpening)spec.Opening).Work;

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) => message.Type == "session-info"
			? null
			: base.Prepare(message.Type == "turn-completed"
				? message with { CompletedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
				: message);

		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) =>
			Owner._sessions.Append(Owner._definition.Id, Owner._context.Workspace, messages);

		protected override void OnSettled(AcpConversationState state, bool terminal) {
			if (terminal) _work.Failed(ConversationId);
		}
	}
}
