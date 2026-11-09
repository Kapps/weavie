using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Workflows;

/// <summary>One answered workflow decision: which provider answered and how long it took.</summary>
/// <param name="Provider">The decision provider id.</param>
/// <param name="DurationMs">The attempt's wall time in milliseconds.</param>
public sealed record WorkflowDecisionRecord(string Provider, long DurationMs);

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
