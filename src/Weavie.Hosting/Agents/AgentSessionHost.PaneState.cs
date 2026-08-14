using System.Text;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal bool TryGetCompletedPlan(string threadId, string turnId, string itemId, out AgentPlan plan) {
		plan = default;
		if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(itemId)) {
			return false;
		}

		string key = AgentPaneIdentity.ItemKey(threadId, turnId, itemId)!;
		lock (_paneGate) {
			if (_paneActiveItems.Contains(key)) {
				return false;
			}

			for (int index = _paneMessages.Count - 1; index >= 0; index--) {
				var message = _paneMessages[index];
				if (!string.Equals(AgentPaneIdentity.ItemKey(message), key, StringComparison.Ordinal)) {
					continue;
				}

				if (message.Type != "item-completed"
					|| !string.Equals(message.ItemType, "plan", StringComparison.Ordinal)
					|| string.IsNullOrWhiteSpace(message.Text)) {
					return false;
				}

				plan = new AgentPlan(key, "Plan", message.Text);
				return true;
			}
		}

		return false;
	}

	private void PublishPaneMessage(AgentPaneMessage message) {
		lock (_paneGate) {
			if (message.Type == "transcript-reset") {
				ResetPaneLocked();
				_paneJournal?.Clear();
				_paneOutput.Reset();
				return;
			}

			var record = StorePaneMessageLocked(message);
			_paneJournal?.Append(message);
			_paneOutput.Live(record);
		}
	}

	private void ReplacePaneSnapshot(IReadOnlyList<AgentPaneMessage> messages) {
		ArgumentNullException.ThrowIfNull(messages);
		lock (_paneGate) {
			ResetPaneLocked();
			_paneJournal?.Clear();
			foreach (var message in messages) {
				StorePaneMessageLocked(message);
				_paneJournal?.Append(message);
			}
			_paneOutput.Reset();
		}
	}

	private void SeedPersistedPane(IReadOnlyList<AgentPaneMessage> persisted) {
		if (persisted.Count == 0) {
			return;
		}

		lock (_paneGate) {
			if (_paneGeneration != 0) {
				return;
			}

			var live = PaneSnapshotLocked();
			ClearPaneLocked();
			_nextPaneOrdinal = -persisted.Count;
			_nextPaneRevision = -persisted.Count;
			foreach (var message in persisted) {
				StorePaneMessageLocked(message);
			}
			foreach (var record in live) {
				RestoreLivePaneRecordLocked(record);
			}
		}
	}

	private void RestoreLivePaneRecordLocked(AgentPaneRecord record) {
		int index = _paneMessages.Count;
		var message = record.Message;
		string? key = AgentPaneIdentity.ItemKey(message);
		if (key is not null && IsDelta(message)) {
			_paneMessages.Add(message with { Text = null });
			_paneItemIndexes[key] = index;
			_paneActiveItems.Add(key);
			var buffer = new PaneDeltaBuffer(message);
			buffer.Text.Append(message.Text);
			_paneDeltaBuffers.Add(index, buffer);
		} else {
			_paneMessages.Add(message);
			if (key is not null) {
				_paneItemIndexes[key] = index;
				if (message.Type == "item-started") _paneActiveItems.Add(key);
			}
		}

		_paneOrdinals.Add(record.Ordinal);
		_paneRevisions.Add(record.Revision);
		_nextPaneOrdinal = Math.Max(_nextPaneOrdinal, record.Ordinal);
		_nextPaneRevision = Math.Max(_nextPaneRevision, record.Revision);
	}

	private List<AgentPaneRecord> PaneSnapshotLocked() => PaneSnapshotLocked(null);

	private List<AgentPaneRecord> PaneSnapshotLocked(long? afterRevision) {
		var snapshot = new List<AgentPaneRecord>(afterRevision is null ? _paneMessages.Count : 0);
		for (int index = 0; index < _paneMessages.Count; index++) {
			if (afterRevision is null || _paneRevisions[index] > afterRevision) {
				snapshot.Add(SnapshotRecordAtLocked(index));
			}
		}

		return snapshot;
	}

	private AgentPaneRecord SnapshotRecordAtLocked(int index) {
		var message = _paneMessages[index];
		if (_paneDeltaBuffers.TryGetValue(index, out var buffer)) {
			message = buffer.Latest with { Text = buffer.Text.ToString() };
		}

		return new AgentPaneRecord(
			_paneGeneration,
			_paneOrdinals[index],
			_paneRevisions[index],
			message);
	}

	private AgentPaneRecord StorePaneMessageLocked(AgentPaneMessage message) {
		string? key = AgentPaneIdentity.ItemKey(message);
		if (key is null) {
			return AppendPaneMessageLocked(message);
		}

		if (message.Type == "item-started") {
			_paneActiveItems.Add(key);
			if (_paneItemIndexes.TryGetValue(key, out int startedIndex)) {
				_paneDeltaBuffers.Remove(startedIndex);
				_paneMessages[startedIndex] = message;
				_paneRevisions[startedIndex] = ++_nextPaneRevision;
				return RecordAtLocked(startedIndex);
			}

			_paneItemIndexes[key] = _paneMessages.Count;
			return AppendPaneMessageLocked(message);
		}

		if (IsDelta(message)) {
			_paneActiveItems.Add(key);
			if (!_paneItemIndexes.TryGetValue(key, out int deltaIndex)) {
				deltaIndex = _paneMessages.Count;
				_paneItemIndexes[key] = deltaIndex;
				AppendPaneMessageLocked(message with { Text = null });
			}
			if (!_paneDeltaBuffers.TryGetValue(deltaIndex, out var buffer)) {
				buffer = new PaneDeltaBuffer(message);
				_paneDeltaBuffers.Add(deltaIndex, buffer);
			}
			buffer.Latest = message;
			buffer.Text.Append(message.Text);
			_paneRevisions[deltaIndex] = ++_nextPaneRevision;
			return new AgentPaneRecord(
				_paneGeneration,
				_paneOrdinals[deltaIndex],
				_paneRevisions[deltaIndex],
				message);
		}

		if (message.Type is "item-completed" or "item-retracted") {
			_paneActiveItems.Remove(key);
			if (_paneItemIndexes.TryGetValue(key, out int completedIndex)) {
				_paneDeltaBuffers.Remove(completedIndex);
				_paneMessages[completedIndex] = message;
				_paneRevisions[completedIndex] = ++_nextPaneRevision;
				return RecordAtLocked(completedIndex);
			}
			_paneItemIndexes[key] = _paneMessages.Count;
			return AppendPaneMessageLocked(message);
		}

		if (!_paneItemIndexes.ContainsKey(key)) _paneItemIndexes[key] = _paneMessages.Count;
		return AppendPaneMessageLocked(message);
	}

	private AgentPaneRecord AppendPaneMessageLocked(AgentPaneMessage message) {
		long ordinal = ++_nextPaneOrdinal;
		long revision = ++_nextPaneRevision;
		_paneMessages.Add(message);
		_paneOrdinals.Add(ordinal);
		_paneRevisions.Add(revision);
		return new AgentPaneRecord(_paneGeneration, ordinal, revision, message);
	}

	private AgentPaneRecord RecordAtLocked(int index) =>
		new(_paneGeneration, _paneOrdinals[index], _paneRevisions[index], _paneMessages[index]);

	private void ResetPaneLocked() {
		_paneGeneration++;
		ClearPaneLocked();
	}

	private void ClearPaneLocked() {
		_paneMessages.Clear();
		_paneOrdinals.Clear();
		_paneRevisions.Clear();
		_paneItemIndexes.Clear();
		_paneActiveItems.Clear();
		_paneDeltaBuffers.Clear();
		_historyReads.Clear();
		_nextPaneOrdinal = 0;
		_nextPaneRevision = 0;
	}

	private static bool IsDelta(AgentPaneMessage message) =>
		message.Type is "agent-message-delta" or "thought-message-delta" or "user-message-delta"
			or "plan-delta" or "command-output-delta";

	private sealed class PaneDeltaBuffer(AgentPaneMessage latest) {
		public AgentPaneMessage Latest { get; set; } = latest;
		public StringBuilder Text { get; } = new();
	}
}
