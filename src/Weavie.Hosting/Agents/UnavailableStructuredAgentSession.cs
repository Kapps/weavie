using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal sealed class UnavailableStructuredAgentSession : IStructuredAgentSession {
	private readonly string _providerId;
	private readonly string _message;
	private readonly IAsyncDisposable _cleanup;
	private bool _started;

	public UnavailableStructuredAgentSession(string providerId, string message, IAsyncDisposable cleanup) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(message);
		ArgumentNullException.ThrowIfNull(cleanup);
		_providerId = providerId;
		_message = message;
		_cleanup = cleanup;
	}

	public event Action<AgentPaneMessage>? PaneMessage;

	public void Start() {
		if (_started) {
			return;
		}

		_started = true;
		Emit();
	}

	public void Submit(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		Emit();
	}

	public void PrefillPrompt(string prompt) {
		ArgumentNullException.ThrowIfNull(prompt);
		Emit();
	}

	public void Interrupt() {
	}

	public void Restart() => Emit();

	public void ResolveApproval(string requestId, string decision) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(decision);
		Emit();
	}

	public void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentNullException.ThrowIfNull(answers);
		Emit();
	}

	public ValueTask DisposeAsync() => _cleanup.DisposeAsync();

	private void Emit() =>
		PaneMessage?.Invoke(new AgentPaneMessage {
			Type = "error",
			ProviderId = _providerId,
			Text = _message,
			Status = "error",
		});
}
