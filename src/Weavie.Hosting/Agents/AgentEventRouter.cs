using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Corrections;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents;

/// <summary>Synchronously routes normalized provider events into one Weavie session's shared consumers.</summary>
public sealed class AgentEventRouter : IAgentEventSink {
	private readonly SessionChangeTracker _changes;
	private readonly ObservedPermissionMode _mode;
	private readonly SessionStatusMachine _status;
	private readonly CorrectionRecorder _corrections;

	/// <summary>Creates the event router for one loaded session.</summary>
	public AgentEventRouter(
		SessionChangeTracker changes,
		ObservedPermissionMode mode,
		SessionStatusMachine status,
		CorrectionRecorder corrections) {
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(mode);
		ArgumentNullException.ThrowIfNull(status);
		ArgumentNullException.ThrowIfNull(corrections);
		_changes = changes;
		_mode = mode;
		_status = status;
		_corrections = corrections;
	}

	/// <inheritdoc/>
	public AgentEventFeedback Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		_changes.Observe(value);
		// After the tracker: at a turn boundary the recorder drains the tracker's correction snapshot.
		_corrections.Observe(value);
		_mode.Observe(value);
		_status.Observe(value);
		var locations = _changes.EditLocationsFor(value);
		return locations.Count == 0
			? AgentEventFeedback.None
			: new AgentEventFeedback { Messages = locations };
	}
}
