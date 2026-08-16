using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private readonly Lock _initialInputGate = new();
	private AgentTurnSubmission? _initialInput;
	private bool _initialInputAssigned;
	private bool _acceptInitialInput = true;

	/// <summary>
	/// Sets the session's first input. A terminal-backed agent takes it as part of its launch — the provider's own
	/// entry point for an opening turn — because a TUI that has not finished starting discards or re-frames written
	/// input, so injecting keystrokes would race its startup and silently lose the prompt. A structured agent has a
	/// real readiness report, so its first turn is submitted over the protocol once the agent goes idle.
	/// </summary>
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
			if (Agent.TerminalSession is { } terminal) {
				terminal.SeedFirstTurn(input);
				return;
			}

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
