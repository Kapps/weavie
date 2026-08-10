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
			if (_paneItemIndexes.ContainsKey(key)) {
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
			_paneItemIndexes.Add(key, index);
			var buffer = new PaneDeltaBuffer(index, message);
			buffer.Text.Append(message.Text);
			_paneDeltaBuffers.Add(key, buffer);
		} else {
			_paneMessages.Add(message);
			if (key is not null && message.Type == "item-started") {
				_paneItemIndexes.Add(key, index);
			}
		}

		_paneOrdinals.Add(record.Ordinal);
		_paneRevisions.Add(record.Revision);
		_paneSerializedRecords.Add(null);
		_nextPaneOrdinal = Math.Max(_nextPaneOrdinal, record.Ordinal);
		_nextPaneRevision = Math.Max(_nextPaneRevision, record.Revision);
	}

	private List<AgentPaneRecord> PaneSnapshotLocked() {
		var snapshot = new List<AgentPaneRecord>(_paneMessages.Count);
		for (int index = 0; index < _paneMessages.Count; index++) {
			snapshot.Add(SnapshotRecordAtLocked(index));
		}

		return snapshot;
	}

	private AgentPaneRecord SnapshotRecordAtLocked(int index) {
		var message = _paneMessages[index];
		foreach (var buffer in _paneDeltaBuffers.Values) {
			if (buffer.Index == index) {
				message = buffer.Latest with { Text = buffer.Text.ToString() };
				break;
			}
		}

		return new AgentPaneRecord(
			_paneGeneration,
			_paneOrdinals[index],
			_paneRevisions[index],
			message);
	}

	private SerializedPaneRecord SerializedRecordAtLocked(int index) {
		var serialized = _paneSerializedRecords[index];
		if (serialized is null || serialized.Record.Revision != _paneRevisions[index]) {
			var record = SnapshotRecordAtLocked(index);
			string json = AgentPaneProtocol.Serialize(record);
			int fragmentBytes = AgentPaneProtocol.Measure(
				new AgentPaneFragment(record, json, 0, json.Length));
			serialized = new SerializedPaneRecord(record, json, fragmentBytes);
			_paneSerializedRecords[index] = serialized;
		}

		return serialized;
	}

	private AgentPaneRecord StorePaneMessageLocked(AgentPaneMessage message) {
		string? key = AgentPaneIdentity.ItemKey(message);
		if (key is null) {
			return AppendPaneMessageLocked(message);
		}

		if (message.Type == "item-started") {
			_paneDeltaBuffers.Remove(key);
			if (_paneItemIndexes.TryGetValue(key, out int startedIndex)) {
				_paneMessages[startedIndex] = message;
				_paneRevisions[startedIndex] = ++_nextPaneRevision;
				_paneSerializedRecords[startedIndex] = null;
				return RecordAtLocked(startedIndex);
			}

			_paneItemIndexes[key] = _paneMessages.Count;
			return AppendPaneMessageLocked(message);
		}

		if (IsDelta(message)) {
			if (!_paneItemIndexes.TryGetValue(key, out int deltaIndex)) {
				deltaIndex = _paneMessages.Count;
				_paneItemIndexes[key] = deltaIndex;
				AppendPaneMessageLocked(message with { Text = null });
			}
			if (!_paneDeltaBuffers.TryGetValue(key, out var buffer)) {
				buffer = new PaneDeltaBuffer(deltaIndex, message);
				_paneDeltaBuffers.Add(key, buffer);
			}
			buffer.Latest = message;
			buffer.Text.Append(message.Text);
			_paneRevisions[deltaIndex] = ++_nextPaneRevision;
			_paneSerializedRecords[deltaIndex] = null;
			return new AgentPaneRecord(
				_paneGeneration,
				_paneOrdinals[deltaIndex],
				_paneRevisions[deltaIndex],
				message);
		}

		if (message.Type == "item-completed" && _paneItemIndexes.Remove(key, out int completedIndex)) {
			_paneDeltaBuffers.Remove(key);
			_paneMessages[completedIndex] = message;
			_paneRevisions[completedIndex] = ++_nextPaneRevision;
			_paneSerializedRecords[completedIndex] = null;
			return RecordAtLocked(completedIndex);
		}

		return AppendPaneMessageLocked(message);
	}

	private AgentPaneRecord AppendPaneMessageLocked(AgentPaneMessage message) {
		long ordinal = ++_nextPaneOrdinal;
		long revision = ++_nextPaneRevision;
		_paneMessages.Add(message);
		_paneOrdinals.Add(ordinal);
		_paneRevisions.Add(revision);
		_paneSerializedRecords.Add(null);
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
		_paneSerializedRecords.Clear();
		_paneItemIndexes.Clear();
		_paneDeltaBuffers.Clear();
		_nextPaneOrdinal = 0;
		_nextPaneRevision = 0;
	}

	private static bool IsDelta(AgentPaneMessage message) =>
		message.Type is "agent-message-delta" or "plan-delta" or "command-output-delta";

	private sealed class PaneDeltaBuffer(int index, AgentPaneMessage latest) {
		public int Index { get; } = index;
		public AgentPaneMessage Latest { get; set; } = latest;
		public StringBuilder Text { get; } = new();
	}

	private sealed record SerializedPaneRecord(AgentPaneRecord Record, string Json, int FragmentBytes);
}
