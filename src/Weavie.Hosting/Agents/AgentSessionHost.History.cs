namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal AgentPaneHistory ReadHistory(AgentPaneHistoryRequest request) {
		lock (_paneGate) {
			if ((request.KnownGeneration is null) != (request.KnownRevision is null)
				|| request.KnownGeneration == _paneGeneration && request.KnownRevision > _nextPaneRevision) {
				throw new ArgumentException("The agent transcript history baseline is invalid.", nameof(request));
			}
			long? afterRevision = request.KnownGeneration == _paneGeneration
				? request.KnownRevision
				: null;
			return new AgentPaneHistory(_paneGeneration, _nextPaneRevision, PaneSnapshotLocked(afterRevision));
		}
	}

	internal AgentPaneRecord ReadRecord(AgentPaneRecordRequest request) {
		lock (_paneGate) {
			int index = _paneOrdinals.BinarySearch(request.Ordinal);
			if (request.Generation != _paneGeneration || index < 0) {
				throw new InvalidOperationException("The agent transcript record no longer exists.");
			}
			return SnapshotRecordAtLocked(index);
		}
	}
}
