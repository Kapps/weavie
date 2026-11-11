using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void Submit(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		AcpConversation primary;
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			primary = _primary;
			if (primary.Normalize(submission) is not { } normalized) return;
			bool reconnect = primary.Failed;
			var continuation = primary.Continuation;
			if (reconnect && continuation.TurnNumber > 0 && continuation.SessionId is not null && !Restorable) {
				throw new InvalidOperationException(
					$"{_definition.Name} cannot restore this conversation. Start a new conversation to continue.");
			}
			primary.Enqueue(normalized);
			if (reconnect) Restart(clearSubmissions: false);
			primary = _primary;
		}
		primary.DispatchPendingSubmission();
	}

	private bool Restorable {
		get { lock (_turnTransitionGate) return _runtime.Features.Load || _runtime.Features.Resume; }
	}

	/// <inheritdoc/>
	public void SetControl(string axis, string value) => Primary.SetControl(axis, value);

	/// <inheritdoc/>
	public Task ApplyControlsAsync(IReadOnlyList<AgentControlValue> values) => Primary.ApplyControlsAsync(values);

	/// <inheritdoc/>
	public void PrefillPrompt(string prompt) => Primary.Prefill(prompt);

	/// <inheritdoc/>
	public void Notice(string text) {
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			_primary.Notice(text);
		}
	}

	/// <inheritdoc/>
	public void Interrupt() {
		lock (_turnTransitionGate) {
			if (_primary.Busy) {
				_primary.Interrupt();
				return;
			}
		}
		lock (_turnTransitionGate) {
			foreach (var conversation in Roots().Where(conversation => conversation.Live && conversation.HasWork)) conversation.Interrupt();
		}
	}

	/// <inheritdoc/>
	public void ResolvePermission(string requestId, string optionId) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(optionId);
		var (conversation, id) = RequestOwner(requestId);
		conversation.ResolvePermission(id, optionId);
	}

	/// <inheritdoc/>
	public void ResolveInput(
		string requestId,
		string action,
		IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(action);
		ArgumentNullException.ThrowIfNull(answers);
		var (conversation, id) = RequestOwner(requestId);
		conversation.ResolveInput(id, action, answers);
	}

	/// <inheritdoc/>
	public void Authenticate(
		string requestId,
		string methodId,
		IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(methodId);
		ArgumentNullException.ThrowIfNull(answers);
		var (conversation, id) = RequestOwner(requestId);
		conversation.Authenticate(id, methodId);
	}

	// Nested request ids are namespaced by their side or subagent conversation; everything else belongs to the primary.
	private (AcpConversation Conversation, string RequestId) RequestOwner(string requestId) {
		int separator = requestId.IndexOf(':', StringComparison.Ordinal);
		if (separator > 0 && NestedConversation(requestId[..separator]) is { } nested) return (nested, requestId[(separator + 1)..]);
		return (Primary, requestId);
	}
}
