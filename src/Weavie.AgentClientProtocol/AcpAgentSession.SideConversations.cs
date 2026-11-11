using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public IReadOnlyList<AgentAside> Asides {
		get {
			lock (_turnTransitionGate) {
				return [.. _sideConversations.Values.Where(side => Continuable(side) || _sides.ContainsKey(side.ConversationId))
					.Select(side => new AgentAside(side.ConversationId, side.InitialPrompt, _sides.GetValueOrDefault(side.ConversationId) is { Settled: false }))];
			}
		}
	}

	/// <inheritdoc/>
	public void AskAside(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		if (submission.Text.Trim().Length == 0 && submission.Attachments.Count == 0) {
			throw new ArgumentException("Write a side question or attach an image.", nameof(submission));
		}
		lock (_turnTransitionGate) {
			EnsureConversationAdmission();
			var primary = _primary;
			EnsureSideConversationSupport(primary);
			var spec = ChildSpec(primary, fork: true, submission.Text, AcpConversationRole.Side);
			StartOwned(spec with { Seed = spec.Seed with { Pending = [primary.Normalize(submission)!] } }, _context.Bindings.Side(spec.Seed.Continuation.ConversationId));
		}
	}

	/// <inheritdoc/>
	public void ReplyAside(string conversationId, AgentTurnSubmission submission) {
		ArgumentException.ThrowIfNullOrEmpty(conversationId);
		ArgumentNullException.ThrowIfNull(submission);
		if (submission.Text.Trim().Length == 0 && submission.Attachments.Count == 0) {
			throw new ArgumentException("Write a side reply or attach an image.", nameof(submission));
		}
		lock (_turnTransitionGate) {
			EnsureConversationAdmission();
			if (NestedConversation(conversationId) is { OwnsWork: true } conversation) {
				if (!conversation.Live) throw new InvalidOperationException("That conversation has ended.");
				conversation.Submit(submission);
				return;
			}
			if (_sideConversations.GetValueOrDefault(conversationId) is not { } state || !Continuable(state)) {
				throw new InvalidOperationException("That side conversation is no longer available.");
			}
			EnsureSideConversationSupport(_primary);
			var seed = AcpConversationHandoff.Fresh(state) with { Pending = [submission] };
			StartOwned(new(seed, AcpConversationOpening.Continue, AcpConversationRole.Side), _context.Bindings.Side(conversationId));
		}
	}

	/// <inheritdoc/>
	public void SetAsideCollapsed(string conversationId, bool collapsed) {
		ArgumentException.ThrowIfNullOrEmpty(conversationId);
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			var marker = Output.View(_definition.Id, conversationId).Rows
				.FirstOrDefault(message => message.Type == "side-conversation-started")
				?? throw new InvalidOperationException("That side conversation is no longer available.");
			_primary.Publish(marker with { Type = "side-conversation-collapsed", Text = null, Summary = null, Status = collapsed ? "collapsed" : "expanded" });
		}
	}

	private void EnsureSideConversationSupport(AcpConversation primary) {
		if (!primary.Ready || !Forkable) {
			throw new InvalidOperationException(
				$"{_definition.Name} does not support context-preserving side conversations.");
		}
		if (primary.Rewinding) throw new InvalidOperationException("Wait for the rewind to finish.");
	}

	private void CompleteSideTurn(string conversationId) {
		AcpConversationHandle? runtime;
		lock (_turnTransitionGate) {
			if (!_sides.Remove(conversationId, out runtime)) return;
			runtime.Conversation.Retire();
			PublishSideTerminal(Side(runtime.Conversation.Continuation));
		}
		DisposeSide(runtime);
	}

	private void SuspendSides(string reason) {
		foreach (var side in Sides()) {
			side.Conversation.TerminalizeForRestart(clearSubmissions: true, reason);
			side.Conversation.Save([]);
			lock (_turnTransitionGate) _sides.Remove(side.Id);
			side.Conversation.SettleInteractions();
			side.Conversation.Retire();
			if (side.Conversation.ThreadId is null) PublishSideTerminal(Side(side.Conversation.Continuation));
			DisposeSide(side);
		}
	}

	private void DisposeSide(AcpConversationHandle runtime) {
		_context.Events.Observe(new AgentConversationRemoved(runtime.Id));
		PublishBackground();
		_ = runtime.DisposeAsync();
	}

	private void PublishSideTerminal(SideConversation side) => _primary.Publish(SideTerminal(side));

	private AgentPaneMessage SideMarker(SideConversation side, string status) =>
		SideMessage(side, "side-conversation-started", status) with { Text = side.InitialPrompt };

	private AgentPaneMessage SideTerminal(SideConversation side) => SideMessage(side, "side-conversation-failed", "failed");

	private AgentPaneMessage SideMessage(SideConversation side, string type, string status) => new() {
		Type = type,
		ProviderId = _definition.Id,
		ThreadId = _primary.ThreadId,
		ConversationId = side.ConversationId,
		AnchorTurnId = side.AnchorTurnNumber.ToString(CultureInfo.InvariantCulture),
		IsPrimaryThread = false,
		Status = status,
	};

	private static bool Continuable(AcpConversationState side) => side is { Failed: false, SessionId: not null };

	private static SideConversation Side(AcpConversationState state) =>
		new(state.ConversationId, state.AnchorTurnNumber, state.InitialPrompt);

	private sealed record SideConversation(string ConversationId, long AnchorTurnNumber, string InitialPrompt);

}
