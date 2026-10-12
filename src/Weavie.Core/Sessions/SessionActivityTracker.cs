using Weavie.Core.Agents;

namespace Weavie.Core.Sessions;

/// <summary>
/// Derives a session's one-line activity (<c>running pnpm test</c>, <c>wants to edit App.tsx</c>) from its agent
/// events and <see cref="SessionStatusMachine"/>. Thread-safe; <see cref="Changed"/> fires off the UI thread.
/// </summary>
public sealed class SessionActivityTracker {
	private readonly Lock _gate = new();
	private readonly SessionStatusMachine _status;
	private AgentActivity? _current;
	private AgentActivity? _pending;

	/// <summary>Creates a tracker that reads <paramref name="status"/> to decide which activity to show.</summary>
	public SessionActivityTracker(SessionStatusMachine status) {
		ArgumentNullException.ThrowIfNull(status);
		_status = status;
	}

	/// <summary>Raised when the tracked activity changes; <see cref="Line"/> also follows status changes.</summary>
	public event Action? Changed;

	/// <summary>The activity line for the current status, or <c>null</c> when there's nothing more specific than the status.</summary>
	public string? Line {
		get {
			var status = _status.Status;
			lock (_gate) {
				return status switch {
					SessionStatus.NeedsInput => _pending?.Wants,
					SessionStatus.Working => _current?.Doing,
					_ => null,
				};
			}
		}
	}

	/// <summary>Feeds one agent event; subagent and side-conversation tool calls count, but only the main turn ends activity.</summary>
	public void Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		bool changed;
		lock (_gate) {
			var (current, pending) = (_current, _pending);
			switch (value is AgentConversationEvent conversation ? conversation.Value : value) {
				case AgentActivityObserved { AwaitingApproval: true } observed:
					_pending = observed.Activity;
					break;
				case AgentActivityObserved observed:
					(_current, _pending) = (observed.Activity, null);
					break;
				case AgentPermissionResolved { RequiresUserInput: false } or AgentToolCompleted:
					_pending = null;
					break;
				case AgentTurnStopped or AgentPromptSubmitted or AgentSessionStarted when value is not AgentConversationEvent:
					(_current, _pending) = (null, null);
					break;
			}
			changed = current != _current || pending != _pending;
		}
		if (changed) Changed?.Invoke();
	}
}
