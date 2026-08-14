using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private readonly Lock _initialInputGate = new();
	private AgentTurnSubmission? _initialInput;
	private bool _initialInputAssigned;
	private bool _acceptInitialInput = true;

	/// <summary>Queues the session's first input until its agent reports that it is ready.</summary>
	internal void QueueInitialInput(AgentTurnSubmission input) {
		ArgumentNullException.ThrowIfNull(input);
		if (input.Text.Trim().Length == 0 && input.Attachments.Count == 0) {
			throw new ArgumentException("Initial agent input must include text or an image.", nameof(input));
		}
		lock (_initialInputGate) {
			ObjectDisposedException.ThrowIf(!_acceptInitialInput, this);
			if (_initialInputAssigned) {
				throw new InvalidOperationException("Initial agent input is already assigned to this session.");
			}

			_initialInputAssigned = true;
			_initialInput = input;
			Status.Changed += DeliverInitialInput;
		}

		DeliverInitialInput(Status.Status);
	}

	private void DeliverInitialInput(SessionStatus status) {
		if (status != SessionStatus.Idle) {
			return;
		}

		AgentTurnSubmission? input;
		lock (_initialInputGate) {
			if (_initialInput is null || Status.Status != SessionStatus.Idle) {
				return;
			}

			input = _initialInput;
			_initialInput = null;
			Status.Changed -= DeliverInitialInput;
		}

		SendAgentInput(input);
	}

	private void DiscardInitialInput() {
		lock (_initialInputGate) {
			_acceptInitialInput = false;
			_initialInput = null;
			Status.Changed -= DeliverInitialInput;
		}
	}
}
