using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal const int HistoryPageTargetBytes = 192 * 1024;
	private const int HistoryPageEnvelopeReserveBytes = 1024;

	internal async Task<AgentPaneHistoryPage> ReadHistoryPageAsync(
		AgentPaneHistoryCursor? cursor,
		CancellationToken ct) {
		await WaitForPaneReadyAsync(ct).ConfigureAwait(false);

		AgentPaneHistoryPage page;
		lock (_paneGate) {
			long generation = _paneGeneration;
			if (cursor is not null && cursor.Generation != generation) {
				throw new InvalidOperationException("The agent transcript changed while its history was loading.");
			}

			int ceiling = cursor?.Ceiling ?? _paneMessages.Count;
			int before = cursor?.Before ?? ceiling;
			int? jsonBefore = cursor?.JsonBefore;
			long? jsonRevision = cursor?.JsonRevision;
			bool restarted = false;
			if (ceiling > _paneMessages.Count
				|| before < 0
				|| before > ceiling
				|| (jsonBefore is null) != (jsonRevision is null)) {
				throw new InvalidOperationException("The agent transcript history cursor is invalid.");
			}
			if (jsonBefore is not null) {
				if (before == 0) {
					throw new InvalidOperationException("The agent transcript history cursor is invalid.");
				}
				var current = SerializedRecordAtLocked(before - 1);
				if (current.Record.Revision != jsonRevision) {
					ceiling = _paneMessages.Count;
					before = ceiling;
					jsonBefore = null;
					jsonRevision = null;
					restarted = true;
				} else if (jsonBefore <= 0
					|| jsonBefore > current.Json.Length) {
					throw new InvalidOperationException("The agent transcript history cursor is invalid.");
				}
			}

			var records = new List<AgentPaneFragment>();
			int bytes = 0;
			while (before > 0) {
				var serialized = SerializedRecordAtLocked(before - 1);
				var record = serialized.Record;
				string json = serialized.Json;
				int jsonEnd = jsonBefore ?? json.Length;
				int available = HistoryPageTargetBytes - HistoryPageEnvelopeReserveBytes - bytes;
				var fragment = jsonBefore is null && serialized.FragmentBytes <= available
					? new AgentPaneFragment(record, json, 0, json.Length)
					: records.Count == 0
						? FitFragment(record, json, jsonEnd, available)
						: null;
				if (fragment is null) {
					if (records.Count > 0) break;
					throw new InvalidOperationException(
						"An agent transcript record fragment cannot fit in a history page.");
				}

				records.Insert(0, fragment);
				bytes += AgentPaneProtocol.Measure(fragment) + 1;
				if (fragment.JsonOffset > 0) {
					jsonBefore = fragment.JsonOffset;
					jsonRevision = record.Revision;
					break;
				}

				before--;
				jsonBefore = null;
				jsonRevision = null;
			}

			page = new AgentPaneHistoryPage(
				generation,
				records,
				before == 0
					? null
					: new AgentPaneHistoryCursor(
						generation,
						ceiling,
						before,
						jsonBefore,
						jsonRevision),
				restarted);
		}

		return page;
	}

	private static AgentPaneFragment? FitFragment(
		AgentPaneRecord record,
		string json,
		int jsonEnd,
		int available) {
		int low = Math.Max(0, jsonEnd - available);
		int high = jsonEnd;
		while (low < high) {
			int middle = low + ((high - low) / 2);
			int start = TextBoundary(json, middle, jsonEnd);
			if (Measure(start) <= available) {
				high = middle;
			} else {
				low = middle + 1;
			}
		}

		int fitted = TextBoundary(json, low, jsonEnd);
		while (fitted < jsonEnd && Measure(fitted) > available) {
			fitted = TextBoundary(json, fitted + 1, jsonEnd);
		}

		return fitted == jsonEnd
			? null
			: new AgentPaneFragment(record, json[fitted..jsonEnd], fitted, json.Length);

		int Measure(int start) => AgentPaneProtocol.Measure(
			new AgentPaneFragment(record, json[start..jsonEnd], start, json.Length));
	}

	private static int TextBoundary(string text, int index, int end) =>
		index < end && char.IsLowSurrogate(text[index]) ? index + 1 : index;
}
