using Weavie.Core.Sessions;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private readonly Lock _initialPromptGate = new();
	private string? _initialPrompt;
	private bool _initialPromptAssigned;
	private bool _acceptInitialPrompt = true;

	/// <summary>Queues the session's first prompt until its agent reports that it is ready for input.</summary>
	internal void QueueInitialPrompt(string prompt) {
		ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
		lock (_initialPromptGate) {
			ObjectDisposedException.ThrowIf(!_acceptInitialPrompt, this);
			if (_initialPromptAssigned) {
				throw new InvalidOperationException("An initial prompt is already assigned to this session.");
			}

			_initialPromptAssigned = true;
			_initialPrompt = prompt;
			Status.Changed += DeliverInitialPrompt;
		}

		DeliverInitialPrompt(Status.Status);
	}

	private void DeliverInitialPrompt(SessionStatus status) {
		if (status != SessionStatus.Idle) {
			return;
		}

		string? prompt;
		lock (_initialPromptGate) {
			if (_initialPrompt is null || Status.Status != SessionStatus.Idle) {
				return;
			}

			prompt = _initialPrompt;
			_initialPrompt = null;
			Status.Changed -= DeliverInitialPrompt;
		}

		SendAgentPrompt(prompt);
	}

	private void DiscardInitialPrompt() {
		lock (_initialPromptGate) {
			_acceptInitialPrompt = false;
			_initialPrompt = null;
			Status.Changed -= DeliverInitialPrompt;
		}
	}
}
