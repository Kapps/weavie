using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Hooks;
using Weavie.Core.Review.Hints;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents;

/// <summary>Synchronously routes normalized provider events into one Weavie session's shared consumers.</summary>
public sealed class AgentEventRouter : IAgentEventSink {
	private readonly SessionChangeTracker _changes;
	private readonly ObservedPermissionMode _mode;
	private readonly SessionStatusMachine _status;
	private readonly SessionActivityTracker _activity;
	private readonly ReviewHintStore _hints;

	/// <summary>Creates the event router for one loaded session.</summary>
	public AgentEventRouter(
		SessionChangeTracker changes,
		ObservedPermissionMode mode,
		SessionStatusMachine status,
		SessionActivityTracker activity,
		ReviewHintStore hints) {
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(mode);
		ArgumentNullException.ThrowIfNull(status);
		ArgumentNullException.ThrowIfNull(activity);
		ArgumentNullException.ThrowIfNull(hints);
		_changes = changes;
		_mode = mode;
		_status = status;
		_activity = activity;
		_hints = hints;
	}

	/// <inheritdoc/>
	public AgentEventFeedback Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		var shared = value is AgentConversationEvent conversation ? conversation.Value : value;
		_changes.Observe(value);
		if (value is not AgentConversationEvent) _mode.Observe(value);
		_status.Observe(value);
		_activity.Observe(value);
		_hints.Observe(value);
		var locations = _changes.EditLocationsFor(shared);
		return locations.Count == 0
			? AgentEventFeedback.None
			: new AgentEventFeedback { Messages = locations };
	}
}
