using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>
/// The only way one ACP conversation reaches its owner. Every call is inert once detached; a staged port publishes
/// nothing until its owner adopts the conversation, but still reports failure and the opened session.
/// </summary>
internal abstract class AcpConversationPort(Lock transitionGate, ConversationView view) : IAgentEventSink {

	public IReadOnlyList<AgentPaneMessage> CompleteContent() {
		lock (transitionGate) return Publishing ? view.CompleteContent() : [];
	}

	internal bool Detached { get; private set; }
	private bool Publishing => !Detached && !Staged;

	public AgentEventFeedback Observe(AgentEvent value) {
		lock (transitionGate) return Publishing ? OnObserve(value) : AgentEventFeedback.None;
	}

	public void Emit(AcpConversationState state, AgentPaneMessage message) {
		lock (transitionGate) if (Publishing) OnEmit(state, message);
	}

	public void Save(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
		lock (transitionGate) if (Publishing) OnSave(state, messages);
	}

	public void ControlsChanged() {
		lock (transitionGate) if (Publishing) OnControlsChanged();
	}

	public void UsageChanged(AgentUsageSnapshot snapshot) {
		lock (transitionGate) if (Publishing) OnUsageChanged(snapshot);
	}

	public void QueueChanged(IReadOnlyList<AgentTurnSubmission> queue) {
		lock (transitionGate) if (Publishing) OnQueueChanged(queue);
	}

	/// <summary>Reports that the conversation has no work left; <paramref name="terminal"/> when it cannot continue.</summary>
	public void Settled(AcpConversationState state, bool terminal) {
		lock (transitionGate) if (Publishing) OnSettled(state, terminal);
	}

	/// <summary>Returns whether the process owner took <paramref name="error"/>; otherwise the conversation fails alone.</summary>
	public bool Fail(Exception error) {
		lock (transitionGate) return !Detached && OnFail(error);
	}

	/// <summary>Reports that the provider session opened, before the conversation restores controls and turns ready.</summary>
	public void Opened() {
		lock (transitionGate) if (!Detached) OnOpened();
	}

	public void RestartProcess(AcpConversation conversation) {
		lock (transitionGate) if (Publishing) OnRestartProcess(conversation);
	}

	public IReadOnlyDictionary<string, string> ControlDefaults() {
		lock (transitionGate) return Publishing ? OnControlDefaults() : new Dictionary<string, string>(StringComparer.Ordinal);
	}


	public void RememberControl(string axis, string value) {
		lock (transitionGate) if (Publishing) OnRememberControl(axis, value);
	}

	public void ForgetControl(string axis, string value) {
		lock (transitionGate) if (Publishing) OnForgetControl(axis, value);
	}

	public void BackgroundChanged() {
		lock (transitionGate) if (Publishing) OnBackgroundChanged();
	}

	/// <summary>Creates the conversation of a subagent announced to this conversation; it publishes through its own port.</summary>
	public AcpConversation CreateSubagent(AcpConversationSpec spec, AgentConversationBinding binding) => OnCreateSubagent(spec, binding);

	public void Closed(AcpConversationState state) {
		lock (transitionGate) if (Publishing) OnClosed(state);
	}

	public void Detach() {
		lock (transitionGate) Detached = true;
	}

	protected virtual bool Staged => false;

	protected virtual void OnOpened() { }
	protected virtual void OnClosed(AcpConversationState state) { }

	protected abstract AgentEventFeedback OnObserve(AgentEvent value);
	protected abstract void OnEmit(AcpConversationState state, AgentPaneMessage message);
	protected abstract void OnSave(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages);
	protected abstract void OnControlsChanged();
	protected abstract void OnUsageChanged(AgentUsageSnapshot snapshot);
	protected abstract void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue);
	protected abstract void OnSettled(AcpConversationState state, bool terminal);
	protected abstract bool OnFail(Exception error);
	protected abstract void OnRestartProcess(AcpConversation conversation);
	protected abstract IReadOnlyDictionary<string, string> OnControlDefaults();
	protected abstract void OnRememberControl(string axis, string value);
	protected abstract void OnForgetControl(string axis, string value);
	protected abstract void OnBackgroundChanged();
	protected abstract AcpConversation OnCreateSubagent(AcpConversationSpec spec, AgentConversationBinding binding);
}

public sealed partial class AcpAgentSession {
	private abstract class OwnedPort(AcpAgentSession owner, AcpRuntime runtime, string? conversationId)
		: AcpConversationPort(runtime.TransitionGate, owner.Output.View(runtime.Definition.Id, conversationId)) {
		protected AcpAgentSession Owner => owner;
		protected AcpRuntime Runtime => runtime;

		protected abstract AgentPaneMessage? Prepare(AgentPaneMessage message);

		protected abstract void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages);

		protected override void OnEmit(AcpConversationState state, AgentPaneMessage message) {
			if (Prepare(message) is not { } prepared) return;
			owner.Output.Write(prepared, () => {
				// Failure reports must still reach the user after the archive stops accepting writes.
				if (prepared.Type == "transcript-reset") return;
				if (!owner._storageFailed) OnSave(state, [prepared]);
				else if (!state.Failed && prepared.Type is not ("side-conversation-closed" or "side-conversation-failed")) throw new AcpSessionStoreException(
					"The ACP conversation cannot continue until storage is available.", new IOException("Conversation storage failed."));
			});
		}

		protected override void OnSave(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
			try {
				Store(state, messages);
			} catch (AcpSessionStoreException error) {
				owner.ArchiveFailed(error);
				throw;
			}
		}

		protected override void OnRestartProcess(AcpConversation conversation) => owner.Restart(clearSubmissions: false);

		protected override IReadOnlyDictionary<string, string> OnControlDefaults() => runtime.ControlDefaults.Resolve(runtime.Definition.Id);

		protected override void OnRememberControl(string axis, string value) => runtime.ControlDefaults.Set(runtime.Definition.Id, axis, value);

		protected override void OnForgetControl(string axis, string value) => runtime.ControlDefaults.Clear(runtime.Definition.Id, axis, value);

		protected override void OnBackgroundChanged() => runtime.UpdateBackground();

		protected override AcpConversation OnCreateSubagent(AcpConversationSpec spec, AgentConversationBinding binding) =>
			runtime.Create(spec, new SubagentPort(owner, runtime, spec), binding);
	}

	private class PrimaryPort(AcpAgentSession owner) : OwnedPort(owner, owner._runtime, null) {
		protected override AgentEventFeedback OnObserve(AgentEvent value) => Owner._context.Events.Observe(value);

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) => message;

		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) =>
			Owner._sessions.Save(Owner._definition.Id, Owner._context.Workspace, state, messages);

		protected override void OnControlsChanged() => Owner.ControlStateChanged?.Invoke(Owner.ControlState);

		protected override void OnUsageChanged(AgentUsageSnapshot snapshot) => Owner.UsageChanged?.Invoke(snapshot);

		protected override void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue) => Owner.QueuedSubmissionsChanged?.Invoke(queue);

		protected override void OnSettled(AcpConversationState state, bool terminal) { }

		protected override bool OnFail(Exception error) => Owner.FailProcess(error);
	}
}
