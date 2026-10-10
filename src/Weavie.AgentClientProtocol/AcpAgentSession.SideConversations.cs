using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void AskAside(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		if (submission.Text.Trim().Length == 0 && submission.Attachments.Count == 0) {
			throw new ArgumentException("Write a side question or attach an image.", nameof(submission));
		}
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			var primary = _primary;
			EnsureSideConversationSupport(primary);
			// Validated above, so normalization cannot leave the question empty.
			submission = primary.Normalize(submission)!;
			long anchor = primary.TurnNumber;
			var runtime = CreateSide(
				NewContinuation(Guid.NewGuid().ToString("N"), anchor, submission.Text, primary.GuidanceSent),
				anchor > 0 ? new ForkFromOpening(primary, MessageId: null) : AcpConversationOpening.Continue);
			runtime.Conversation.Publish(SideMarker(runtime.Side, "forking"));
			try {
				StartSide(runtime);
				runtime.Conversation.Submit(submission);
			} catch (Exception error) {
				runtime.Conversation.Terminate(error);
			}
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
			ObjectDisposedException.ThrowIf(_disposed, this);
			SideRuntime? runtime;
			lock (_gate) _sides.TryGetValue(conversationId, out runtime);
			var state = _sideConversations.GetValueOrDefault(conversationId);
			if (runtime is null && (state is null || state.Failed || state.SessionId is null)) {
				throw new InvalidOperationException("That side conversation is no longer available.");
			}
			EnsureSideConversationSupport(_primary);
			if (runtime is null) {
				runtime = CreateSide(state!, AcpConversationOpening.Continue);
				StartSide(runtime);
			}
			runtime.Conversation.Submit(submission);
		}
	}

	private void EnsureSideConversationSupport(AcpConversation primary) {
		if (!primary.Ready || !Forkable) {
			throw new InvalidOperationException(
				$"{_definition.Name} does not support context-preserving side conversations.");
		}
		if (primary.Rewinding) throw new InvalidOperationException("Wait for the rewind to finish.");
	}

	private SideRuntime CreateSide(AcpConversationState continuation, AcpConversationOpening opening) {
		var side = new SideConversation(continuation.ConversationId, continuation.AnchorTurnNumber, continuation.InitialPrompt);
		var conversation = new AcpConversation(
			_host, new AcpConversationSpec(AcpConversationHandoff.Fresh(continuation), opening, AcpConversationRole.Side), new SidePort(this, side));
		var runtime = new SideRuntime(conversation, side);
		lock (_gate) _sides.Add(side.ConversationId, runtime);
		return runtime;
	}

	private void StartSide(SideRuntime runtime) => Open(runtime.Conversation, _process!);

	private AcpAgentFeatures Features {
		get { lock (_gate) return _features; }
	}

	private void CompleteSideTurn(string conversationId) {
		SideRuntime? runtime;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (!_sides.Remove(conversationId, out runtime)) return;
			}
			runtime.Conversation.Retire();
			PublishSideTerminal(runtime.Side);
		}
		DisposeSide(runtime);
	}

	private void SuspendSides(string reason) {
		foreach (var side in Sides()) {
			side.Conversation.TerminalizeForRestart(clearSubmissions: true, reason);
			side.Conversation.Save([]);
			lock (_gate) _sides.Remove(side.Side.ConversationId);
			side.Conversation.Retire();
			if (side.Conversation.ThreadId is null) PublishSideTerminal(side.Side);
			DisposeSide(side);
		}
	}

	private void DisposeSide(SideRuntime runtime) {
		_context.Events.Observe(new AgentConversationRemoved(runtime.Side.ConversationId));
		Primary.Run(() => runtime.Conversation.CloseAsync(static () => Task.CompletedTask));
	}

	private void PublishSideTerminal(SideConversation side) => _primary.Publish(SideTerminal(side));

	private AgentPaneMessage SideMarker(SideConversation side, string status) => new() {
		Type = "side-conversation-started",
		ProviderId = _definition.Id,
		ThreadId = _primary.ThreadId,
		ConversationId = side.ConversationId,
		AnchorTurnId = side.AnchorTurnNumber.ToString(CultureInfo.InvariantCulture),
		IsPrimaryThread = false,
		Text = side.InitialPrompt,
		Status = status,
	};

	private AgentPaneMessage SideTerminal(SideConversation side) => new() {
		Type = "side-conversation-failed",
		ProviderId = _definition.Id,
		ThreadId = _primary.ThreadId,
		ConversationId = side.ConversationId,
		AnchorTurnId = side.AnchorTurnNumber.ToString(CultureInfo.InvariantCulture),
		IsPrimaryThread = false,
		Status = "failed",
	};

	private sealed record SideConversation(string ConversationId, long AnchorTurnNumber, string InitialPrompt);

	private sealed record SideRuntime(AcpConversation Conversation, SideConversation Side);
}
