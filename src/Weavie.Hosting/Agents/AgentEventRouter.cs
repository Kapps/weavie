using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents;

/// <summary>Synchronously routes normalized provider events into one Weavie session's shared consumers.</summary>
public sealed class AgentEventRouter : IAgentEventSink {
	private readonly SessionChangeTracker _changes;
	private readonly ObservedPermissionMode _mode;
	private readonly SessionStatusMachine _status;

	/// <summary>Creates the event router for one loaded session.</summary>
	public AgentEventRouter(
		SessionChangeTracker changes,
		ObservedPermissionMode mode,
		SessionStatusMachine status) {
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(mode);
		ArgumentNullException.ThrowIfNull(status);
		_changes = changes;
		_mode = mode;
		_status = status;
	}

	/// <inheritdoc/>
	public AgentEventFeedback Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		_changes.Observe(value);
		_mode.Observe(value);
		_status.Observe(value);
		string? location = _changes.EditLocationFor(value);
		return location is null
			? AgentEventFeedback.None
			: new AgentEventFeedback { Messages = [location] };
	}
}
