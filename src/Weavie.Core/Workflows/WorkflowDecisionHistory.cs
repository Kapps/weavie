using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference.Decisions;

namespace Weavie.Core.Workflows;

/// <summary>One answered workflow decision: which provider answered and how long it took.</summary>
/// <param name="Provider">The decision provider id.</param>
/// <param name="DurationMs">The attempt's wall time in milliseconds.</param>
public sealed record WorkflowDecisionRecord(string Provider, long DurationMs);

/// <summary>The agent's share of a workspace's recent answered workflow decisions.</summary>
/// <param name="AgentAnswered">How many the agent answered.</param>
/// <param name="AgentMedian">Their median duration, or zero when none.</param>
public sealed record DecisionHistorySummary(int AgentAnswered, TimeSpan AgentMedian);

/// <summary>
/// A workspace's most recent answered workflow decisions, oldest first, at
/// <c>~/.weavie/workspaces/&lt;id&gt;/decision-history.json</c>. Shared by every session of the workspace.
/// </summary>
public sealed partial class WorkflowDecisionHistory : JsonDocumentStore {
	/// <summary>How many answered decisions the history keeps.</summary>
	public const int Capacity = 20;

	private readonly List<WorkflowDecisionRecord> _records = [];

	/// <summary>Creates the history over <paramref name="path"/>, loading any persisted records now.</summary>
	public WorkflowDecisionHistory(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Raised after a decision is appended (outside the lock).</summary>
	public event Action? Changed;

	/// <summary>The kept decisions, oldest first.</summary>
	public IReadOnlyList<WorkflowDecisionRecord> Recent {
		get {
			lock (Gate) {
				return [.. _records];
			}
		}
	}

	/// <summary>How many kept decisions the agent answered, and their median time.</summary>
	public DecisionHistorySummary Summary() {
		long[] agent = [.. Recent.Where(record => record.Provider == DecisionProviderIds.Agent).Select(record => record.DurationMs).Order()];
		if (agent.Length == 0) {
			return new DecisionHistorySummary(0, TimeSpan.Zero);
		}
		int middle = agent.Length / 2;
		double median = agent.Length % 2 == 1 ? agent[middle] : (agent[middle - 1] + agent[middle]) / 2.0;
		return new DecisionHistorySummary(agent.Length, TimeSpan.FromMilliseconds(median));
	}

	/// <summary>Records one answered decision, dropping the oldest beyond <see cref="Capacity"/>.</summary>
	public void Append(string provider, TimeSpan duration) {
		ArgumentException.ThrowIfNullOrWhiteSpace(provider);
		lock (Gate) {
			_records.Add(new WorkflowDecisionRecord(provider, (long)duration.TotalMilliseconds));
			if (_records.Count > Capacity) {
				_records.RemoveAt(0);
			}
			PersistLocked();
		}
		Changed?.Invoke();
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		_records.Clear();
		if (text is not null) {
			_records.AddRange(JsonSerializer.Deserialize(text, HistoryJson.Default.ListWorkflowDecisionRecord)
				?? throw new JsonException("The decision history is not a list."));
		}
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(_records, HistoryJson.Default.ListWorkflowDecisionRecord);

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(List<WorkflowDecisionRecord>))]
	private sealed partial class HistoryJson : JsonSerializerContext;
}
