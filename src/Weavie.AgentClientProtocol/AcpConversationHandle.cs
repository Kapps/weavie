using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

/// <summary>An owner's address for one controller; lifecycle and close state remain in the controller and runtime.</summary>
internal sealed class AcpConversationHandle(AcpRuntime runtime, AcpConversation conversation, ConversationView view) : IAgentConversation {
	internal AcpConversation Conversation => conversation;
	public string Id => conversation.ConversationId;
	public ConversationView View => view;
	public AgentControlState ControlState {
		get {
			var snapshot = conversation.Snapshot;
			return new() { Ready = snapshot.Ready, Rewindable = false, Axes = snapshot.Axes, Slash = snapshot.Commands };
		}
	}
	public Task<AgentConversationOutcome> Completion => conversation.Completion;
	public bool Settled => conversation.Settled;

	public void Submit(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		lock (runtime.TransitionGate) {
			RequireLive();
			conversation.Submit(submission);
		}
	}

	public void Interrupt() {
		lock (runtime.TransitionGate) {
			RequireLive();
			conversation.Interrupt();
		}
	}

	public Task ApplyControlsAsync(IReadOnlyList<AgentControlValue> values) {
		lock (runtime.TransitionGate) {
			RequireLive();
			return conversation.ApplyControlsAsync(values);
		}
	}

	public ValueTask DisposeAsync() => new(runtime.CloseAsync(conversation));

	private void RequireLive() {
		if (!conversation.Live) throw new InvalidOperationException("The owned conversation has ended.");
	}
}
