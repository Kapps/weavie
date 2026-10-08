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
		get { lock (_gate) return _features.Load || _features.Resume; }
	}

	/// <inheritdoc/>
	public void SetControl(string axis, string value) => Primary.SetControl(axis, value);

	/// <inheritdoc/>
	public void PrefillPrompt(string prompt) => Primary.Prefill(prompt);

	/// <inheritdoc/>
	public void Interrupt() {
		lock (_turnTransitionGate) {
			var primary = _primary;
			var activeSides = primary.Busy ? [] : Sides().Where(side => side.Conversation.HasWork).ToArray();
			if (activeSides.Length == 0) primary.Interrupt();
			foreach (var side in activeSides) side.Conversation.Interrupt();
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

	// Side request ids are namespaced by their conversation; everything else belongs to the primary.
	private (AcpConversation Conversation, string RequestId) RequestOwner(string requestId) {
		int separator = requestId.IndexOf(':', StringComparison.Ordinal);
		lock (_gate) {
			return separator > 0 && _sides.TryGetValue(requestId[..separator], out var side)
				? (side.Conversation, requestId[(separator + 1)..])
				: (_primary, requestId);
		}
	}
}
