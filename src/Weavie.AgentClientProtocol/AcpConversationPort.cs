using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>The only way one ACP conversation reaches its owner; every call is inert once detached.</summary>
internal abstract class AcpConversationPort(Lock transitionGate) : IAgentEventSink {
	private bool _detached;

	public AgentEventFeedback Observe(AgentEvent value) {
		lock (transitionGate) return _detached ? AgentEventFeedback.None : OnObserve(value);
	}

	public void Emit(AcpConversationState state, AgentPaneMessage message) {
		lock (transitionGate) if (!_detached) OnEmit(state, message);
	}

	public void Save(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
		lock (transitionGate) if (!_detached) OnSave(state, messages);
	}

	public void ControlsChanged() {
		lock (transitionGate) if (!_detached) OnControlsChanged();
	}

	public void UsageChanged(AgentUsageSnapshot snapshot) {
		lock (transitionGate) if (!_detached) OnUsageChanged(snapshot);
	}

	public void QueueChanged(IReadOnlyList<AgentTurnSubmission> queue) {
		lock (transitionGate) if (!_detached) OnQueueChanged(queue);
	}

	/// <summary>Reports that the conversation has no work left; <paramref name="terminal"/> when it cannot continue.</summary>
	public void Settled(bool terminal) {
		lock (transitionGate) if (!_detached) OnSettled(terminal);
	}

	/// <summary>Returns whether the process owner took <paramref name="error"/>; otherwise the conversation fails alone.</summary>
	public bool Fail(Exception error) {
		lock (transitionGate) return !_detached && OnFail(error);
	}

	public void RestartProcess() {
		lock (transitionGate) if (!_detached) OnRestartProcess();
	}

	public IReadOnlyDictionary<string, string> ControlDefaults() {
		lock (transitionGate) return _detached ? new Dictionary<string, string>(StringComparer.Ordinal) : OnControlDefaults();
	}

	public void RememberControl(string axis, string value) {
		lock (transitionGate) if (!_detached) OnRememberControl(axis, value);
	}

	public void ForgetControl(string axis, string value) {
		lock (transitionGate) if (!_detached) OnForgetControl(axis, value);
	}

	public void Detach() {
		lock (transitionGate) _detached = true;
	}

	protected abstract AgentEventFeedback OnObserve(AgentEvent value);
	protected abstract void OnEmit(AcpConversationState state, AgentPaneMessage message);
	protected abstract void OnSave(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages);
	protected abstract void OnControlsChanged();
	protected abstract void OnUsageChanged(AgentUsageSnapshot snapshot);
	protected abstract void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue);
	protected abstract void OnSettled(bool terminal);
	protected abstract bool OnFail(Exception error);
	protected abstract void OnRestartProcess();
	protected abstract IReadOnlyDictionary<string, string> OnControlDefaults();
	protected abstract void OnRememberControl(string axis, string value);
	protected abstract void OnForgetControl(string axis, string value);
}

public sealed partial class AcpAgentSession {
	private abstract class OwnedPort(AcpAgentSession owner) : AcpConversationPort(owner._turnTransitionGate) {
		protected AcpAgentSession Owner => owner;

		protected abstract AgentPaneMessage? Prepare(AgentPaneMessage message);

		protected abstract void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages);

		protected override void OnEmit(AcpConversationState state, AgentPaneMessage message) {
			if (Prepare(message) is not { } prepared) return;
			// The runtime stops on a storage failure; its failure report must still reach the user.
			if (prepared.Type != "transcript-reset") {
				if (!owner._storageFailed) OnSave(state, [prepared]);
				else if (!owner._primary.Failed) throw new AcpSessionStoreException(
					"The ACP conversation cannot continue until storage is available.", new IOException("Conversation storage failed."));
			}
			owner.PaneMessage?.Invoke(prepared);
		}

		protected override void OnSave(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
			try {
				Store(state, messages);
			} catch (AcpSessionStoreException) {
				owner._storageFailed = true;
				throw;
			}
		}

		protected override void OnRestartProcess() => owner.Restart(clearSubmissions: false);

		protected override IReadOnlyDictionary<string, string> OnControlDefaults() => owner._controlDefaults.Resolve(owner._definition.Id);

		protected override void OnRememberControl(string axis, string value) => owner._controlDefaults.Set(owner._definition.Id, axis, value);

		protected override void OnForgetControl(string axis, string value) => owner._controlDefaults.Clear(owner._definition.Id, axis, value);
	}

	private sealed class PrimaryPort(AcpAgentSession owner) : OwnedPort(owner) {
		protected override AgentEventFeedback OnObserve(AgentEvent value) => Owner._context.Events.Observe(value);

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) => message;

		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) =>
			Owner._sessions.Save(Owner._definition.Id, Owner._context.Workspace, state, messages);

		protected override void OnControlsChanged() => Owner.ControlStateChanged?.Invoke(Owner.ControlState);

		protected override void OnUsageChanged(AgentUsageSnapshot snapshot) => Owner.UsageChanged?.Invoke(snapshot);

		protected override void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue) => Owner.QueuedSubmissionsChanged?.Invoke(queue);

		protected override void OnSettled(bool terminal) { }

		protected override bool OnFail(Exception error) => Owner.FailProcess(error);
	}

	private sealed class SidePort(AcpAgentSession owner, SideConversation conversation) : OwnedPort(owner) {
		protected override AgentEventFeedback OnObserve(AgentEvent value) =>
			value is AgentProcessChanged or AgentSessionStarted or AgentRuntimeFailed
				? AgentEventFeedback.None
				: Owner._context.Events.Observe(new AgentConversationEvent(conversation.ConversationId, value));

		protected override AgentPaneMessage? Prepare(AgentPaneMessage message) {
			if (message.Type is "transcript-reset" or "draft") return null;
			string? original = message.RequestId;
			string? requestId = original is { Length: > 0 } ? conversation.ConversationId + ":" + original : null;
			string? itemId = message.ItemId;
			if (requestId is not null) {
				itemId = itemId == original ? requestId : itemId == "request:" + original ? "request:" + requestId : itemId;
			}
			return message with {
				ConversationId = conversation.ConversationId,
				AnchorTurnId = conversation.AnchorTurnNumber.ToString(CultureInfo.InvariantCulture),
				IsPrimaryThread = false,
				RequestId = requestId,
				ItemId = itemId,
			};
		}

		protected override void Store(AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
			Owner._sessions.Save(Owner._definition.Id, Owner._context.Workspace, state, messages);
			Owner._sideConversations[state.ConversationId] = state;
		}

		protected override void OnControlsChanged() { }

		protected override void OnUsageChanged(AgentUsageSnapshot snapshot) { }

		protected override void OnQueueChanged(IReadOnlyList<AgentTurnSubmission> queue) { }

		protected override void OnSettled(bool terminal) {
			if (terminal) Owner.CompleteSideTurn(conversation.ConversationId);
		}

		protected override bool OnFail(Exception error) => error is not AcpRequestException && Owner.FailProcess(error);
	}
}
