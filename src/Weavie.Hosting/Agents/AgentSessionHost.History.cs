using Weavie.Core.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal const int HistoryPageTargetBytes = 192 * 1024;
	private const int HistoryPageEnvelopeReserveBytes = 1024;
	private const int HistoryPageContentBytes = HistoryPageTargetBytes - HistoryPageEnvelopeReserveBytes;

	internal Task<AgentPaneHistoryPage> ReadHistoryPageAsync(
		AgentPaneHistoryCursor? cursor,
		CancellationToken ct) =>
		ReadHistoryPageAsync(new AgentPaneHistoryRequest(cursor, null, null), _directHistoryReader, ct);

	internal Task<AgentPaneHistoryPage> ReadHistoryPageFromBaselineAsync(
		AgentPaneHistoryRequest request,
		CancellationToken ct) =>
		ReadHistoryPageAsync(request, _directHistoryReader, ct);

	internal Task<AgentPaneHistoryPage> ReadHistoryPageAsync(
		AgentPaneHistoryRequest request,
		MessagePeer reader,
		CancellationToken ct) =>
		ReadHistoryPageAsync(request, (object)reader, ct);

	internal void ReleaseHistoryReader(MessagePeer reader) =>
		ReleaseHistoryReader((object)reader, null);

	internal void ReleaseHistoryReader(MessagePeer reader, string readId) {
		ArgumentException.ThrowIfNullOrEmpty(readId);
		ReleaseHistoryReader((object)reader, readId);
	}

	private async Task<AgentPaneHistoryPage> ReadHistoryPageAsync(
		AgentPaneHistoryRequest request,
		object reader,
		CancellationToken ct) {
		await WaitForPaneReadyAsync(ct).ConfigureAwait(false);
		ct.ThrowIfCancellationRequested();

		HistoryRead read;
		var cursor = request.Cursor;
		lock (_paneGate) {
			if (cursor is null) {
				if ((request.KnownGeneration is null) != (request.KnownRevision is null)
					|| request.KnownGeneration == _paneGeneration && request.KnownRevision > _nextPaneRevision) {
					throw new InvalidOperationException("The agent transcript history baseline is invalid.");
				}
				long? afterRevision = request.KnownGeneration == _paneGeneration
					? request.KnownRevision
					: null;
				read = new HistoryRead(
					_paneGeneration,
					_nextPaneRevision,
					PaneSnapshotLocked(afterRevision));
				_historyReads[reader] = read;
			} else if (request.KnownGeneration is not null || request.KnownRevision is not null) {
				throw new InvalidOperationException("The agent transcript history baseline is invalid.");
			} else if (!_historyReads.TryGetValue(reader, out read!)
				|| !string.Equals(read.Id, cursor.ReadId, StringComparison.Ordinal)) {
				throw new InvalidOperationException("The agent transcript history cursor is invalid.");
			}
		}

		AgentPaneHistoryPage page;
		try {
			page = read.ReadPage(cursor);
			ct.ThrowIfCancellationRequested();
		} catch {
			ReleaseHistoryReader(reader, read.Id);
			throw;
		}

		if (page.Cursor is null) {
			ReleaseHistoryReader(reader, read.Id);
		}
		return page;
	}

	private void ReleaseHistoryReader(object reader, string? readId) {
		lock (_paneGate) {
			if (_historyReads.TryGetValue(reader, out var read)
				&& (readId is null || string.Equals(read.Id, readId, StringComparison.Ordinal))) {
				_historyReads.Remove(reader);
			}
		}
	}

	private sealed class HistoryRead(
		long generation,
		long revision,
		IReadOnlyList<AgentPaneRecord> records) {
		private readonly object _gate = new();
		private SerializedPaneRecord? _fragmented;

		public string Id { get; } = Guid.NewGuid().ToString("n");

		public AgentPaneHistoryPage ReadPage(AgentPaneHistoryCursor? cursor) {
			lock (_gate) {
				int before = cursor?.Before ?? records.Count;
				int? jsonBefore = cursor?.JsonBefore;
				if (before < 0
					|| before > records.Count
					|| jsonBefore is not null && before == 0) {
					throw new InvalidOperationException("The agent transcript history cursor is invalid.");
				}

				var fragments = new List<AgentPaneFragment>();
				int bytes = 0;
				while (before > 0) {
					int index = before - 1;
					var serialized = SerializedRecordAt(index);
					var record = serialized.Record;
					string json = serialized.Json;
					int jsonEnd = jsonBefore ?? json.Length;
					if (jsonEnd <= 0 || jsonEnd > json.Length) {
						throw new InvalidOperationException("The agent transcript history cursor is invalid.");
					}

					int available = HistoryPageContentBytes - bytes;
					var sized = jsonBefore is null
						&& serialized.FragmentBytes is { } fragmentBytes
						&& fragmentBytes <= available
							? new SizedFragment(
								new AgentPaneFragment(record, json, 0, json.Length),
								fragmentBytes)
							: fragments.Count == 0
								? FitFragment(record, json, jsonEnd, available)
								: null;
					if (sized is null) {
						if (fragments.Count > 0) break;
						throw new InvalidOperationException(
							"An agent transcript record fragment cannot fit in a history page.");
					}

					fragments.Add(sized.Fragment);
					bytes += sized.Bytes + 1;
					if (sized.Fragment.JsonOffset > 0) {
						jsonBefore = sized.Fragment.JsonOffset;
						break;
					}

					if (_fragmented?.Index == index) {
						_fragmented = null;
					}
					before--;
					jsonBefore = null;
				}

				fragments.Reverse();
				return new AgentPaneHistoryPage(
					Id,
					generation,
					revision,
					fragments,
					before == 0 ? null : new AgentPaneHistoryCursor(Id, before, jsonBefore));
			}
		}

		private SerializedPaneRecord SerializedRecordAt(int index) {
			if (_fragmented is { } cached && cached.Index == index) {
				return cached;
			}

			var record = records[index];
			string json = AgentPaneProtocol.Serialize(record);
			int? fragmentBytes = json.Length <= HistoryPageContentBytes
				? AgentPaneProtocol.Measure(new AgentPaneFragment(record, json, 0, json.Length))
				: null;
			var serialized = new SerializedPaneRecord(index, record, json, fragmentBytes);
			if (fragmentBytes is null or > HistoryPageContentBytes) {
				_fragmented = serialized;
			}
			return serialized;
		}
	}

	private static SizedFragment? FitFragment(
		AgentPaneRecord record,
		string json,
		int jsonEnd,
		int available) {
		int start = jsonEnd;
		int contentBytes = 0;
		int measured = 0;
		while (start > 0) {
			int candidate = start - 1;
			int nextContentBytes = contentBytes + AgentPaneProtocol.MeasureSerializedJsonCharacter(json[candidate]);
			int nextMeasured = AgentPaneProtocol.MeasureEnvelope(record, candidate, json.Length) + nextContentBytes;
			if (nextMeasured > available) {
				break;
			}
			start = candidate;
			contentBytes = nextContentBytes;
			measured = nextMeasured;
		}

		return start == jsonEnd
			? null
			: new SizedFragment(
				new AgentPaneFragment(record, json[start..jsonEnd], start, json.Length),
				measured);
	}

	private sealed record SerializedPaneRecord(
		int Index,
		AgentPaneRecord Record,
		string Json,
		int? FragmentBytes);

	private sealed record SizedFragment(AgentPaneFragment Fragment, int Bytes);
}
