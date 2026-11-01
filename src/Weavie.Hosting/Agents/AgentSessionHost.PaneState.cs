using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	private void PublishPaneMessage(AgentPaneMessage message) {
		lock (_paneGate) {
			if (message.Type == "transcript-reset") {
				ResetPaneLocked();
				_paneOutput.Reset();
				PublishPlanDocumentsLocked();
				return;
			}

			_paneOutput.Live(StorePaneMessageLocked(message));
			if (message.ItemType == "plan" || message.Type == "item-retracted") PublishPlanDocumentsLocked();
		}
	}

	// No client can address the session yet, so the saved transcript reaches pages only through paged history.
	private void LoadSavedTranscript(IReadOnlyList<AgentPaneMessage> messages) {
		lock (_paneGate) {
			foreach (var message in messages) StorePaneMessageLocked(message);
		}
	}

	/// <summary>Publishes the restored plans to the session's restored editor tabs, then starts the runtime.</summary>
	internal void StartStructured() {
		if (Structured is null) return;
		lock (_paneGate) PublishPlanDocumentsLocked();
		Structured.Start();
	}

	private void RestorePaneSnapshot(IReadOnlyList<AgentPaneMessage> messages) {
		ArgumentNullException.ThrowIfNull(messages);
		lock (_paneGate) {
			// Filling an empty pane is not a new epoch. A generation change tells every client its ordinals are
			// void and it must re-fetch; restoring a transcript into a pane that holds nothing invalidates
			// nothing, so the restore streams into the current generation instead.
			if (_pane.Count == 0) {
				foreach (var message in messages) {
					_paneOutput.Live(StorePaneMessageLocked(message));
				}

				PublishPlanDocumentsLocked();
				return;
			}

			// Replacing content does void those ordinals. paneReset says so, but it is a broadcast: a page that
			// misses it is stranded, and a reset alone leaves nothing else to notice. So the restored records
			// also go out live in the new generation -- observing one carries the same meaning, and that path
			// needs no delivery guarantee. Reset first: it discards anything still buffered from the old epoch.
			ResetPaneLocked();
			_paneOutput.Reset();
			foreach (var message in messages) {
				_paneOutput.Live(StorePaneMessageLocked(message));
			}
			PublishPlanDocumentsLocked();
		}
	}

	private List<AgentPaneRecord> PaneSnapshotLocked() => PaneSnapshotLocked(null);

	private List<AgentPaneRecord> PaneSnapshotLocked(long? afterRevision) {
		var snapshot = new List<AgentPaneRecord>(afterRevision is null ? _pane.Count : 0);
		for (int index = 0; index < _pane.Count; index++) {
			if (afterRevision is null || _pane.RevisionAt(index) > afterRevision) {
				snapshot.Add(SnapshotRecordAtLocked(index));
			}
		}

		return snapshot;
	}

	private AgentPaneRecord SnapshotRecordAtLocked(int index) => RecordLocked(_pane.EntryAt(index));

	private AgentPaneRecord StorePaneMessageLocked(AgentPaneMessage message) => RecordLocked(_pane.Store(message));

	private AgentPaneRecord RecordLocked(AgentPaneLedgerEntry entry) =>
		new(_paneGeneration, entry.Ordinal, entry.Revision, entry.Message);

	private void ResetPaneLocked() {
		_paneGeneration++;
		_pane.Clear();
	}
}
