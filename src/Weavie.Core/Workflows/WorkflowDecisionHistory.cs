using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;

namespace Weavie.Core.Workflows;

/// <summary>How many recent workflow decisions the agent answered, and their median time.</summary>
/// <param name="AgentAnswered">How many the agent answered.</param>
/// <param name="AgentMedian">Their median duration, or zero when none.</param>
public sealed record DecisionHistorySummary(int AgentAnswered, TimeSpan AgentMedian);

/// <summary>
/// The durations in milliseconds of a workspace's last 20 agent-answered workflow decisions, oldest first, at
/// <c>~/.weavie/workspaces/&lt;id&gt;/decision-history.json</c>. Shared by every session of the workspace.
/// </summary>
public sealed partial class WorkflowDecisionHistory : JsonDocumentStore {
	private const int Capacity = 20;

	private readonly List<long> _durations = [];

	/// <summary>Creates the history over <paramref name="path"/>, loading any persisted durations now.</summary>
	public WorkflowDecisionHistory(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Raised after a decision is appended (outside the lock).</summary>
	public event Action? Changed;

	/// <summary>How many kept decisions there are, and their median time.</summary>
	public DecisionHistorySummary Summary() {
		long[] sorted;
		lock (Gate) {
			sorted = [.. _durations.Order()];
		}
		if (sorted.Length == 0) {
			return new DecisionHistorySummary(0, TimeSpan.Zero);
		}
		int middle = sorted.Length / 2;
		double median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
		return new DecisionHistorySummary(sorted.Length, TimeSpan.FromMilliseconds(median));
	}

	/// <summary>Records the decision behind <paramref name="receipt"/> when the agent answered it; a decision model's is not kept.</summary>
	public void Record(InferenceReceipt receipt) {
		ArgumentNullException.ThrowIfNull(receipt);
		if (receipt.ProviderId == DecisionProviderIds.Agent) Append(receipt.Duration);
	}

	/// <summary>Records one agent-answered decision, dropping the oldest beyond the last 20.</summary>
	public void Append(TimeSpan duration) {
		lock (Gate) {
			_durations.Add((long)duration.TotalMilliseconds);
			if (_durations.Count > Capacity) {
				_durations.RemoveAt(0);
			}
			PersistLocked();
		}
		Changed?.Invoke();
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		_durations.Clear();
		if (text is not null) {
			_durations.AddRange(JsonSerializer.Deserialize(text, HistoryJson.Default.ListInt64)
				?? throw new JsonException("The decision history is not a list."));
		}
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(_durations, HistoryJson.Default.ListInt64);

	[JsonSerializable(typeof(List<long>))]
	private sealed partial class HistoryJson : JsonSerializerContext;
}
