using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Commands;

/// <summary>A decaying command-choice signal; an empty query is overall usage.</summary>
public sealed record CommandUsage(string Id, string Query, double Weight, long LastUsed);

/// <summary>Workspace-owned command learning, shared by palette and slash completion.</summary>
public sealed partial class CommandUsageStore : JsonDocumentStore {
	/// <summary>The shared decay policy sent to clients for aging snapshots between updates.</summary>
	public static double HalfLifeMilliseconds => TimeSpan.FromDays(7).TotalMilliseconds;

	private readonly Dictionary<(string, string), CommandUsage> _entries = [];

	/// <summary>Loads the workspace's command usage.</summary>
	public CommandUsageStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Records an executed choice, without command arguments or prompt text.</summary>
	public void Record(string id, string query, long now) {
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentNullException.ThrowIfNull(query);
		lock (Gate) {
			Add(id, "", now);
			string normalized = query.Trim().ToLowerInvariant();
			if (normalized.Length > 0) Add(id, normalized, now);
			PersistLocked();
		}
	}

	/// <summary>Returns decayed signals as of the supplied Unix millisecond timestamp.</summary>
	public IReadOnlyList<CommandUsage> Snapshot(long now) {
		lock (Gate) return _entries.Values.Select(entry => entry with { Weight = WeightAt(entry, now) }).ToArray();
	}

	/// <summary>Clears all learned choices.</summary>
	public void Clear() {
		lock (Gate) {
			_entries.Clear();
			PersistLocked();
		}
	}

	private void Add(string id, string query, long now) {
		var key = (id, query);
		double weight = _entries.TryGetValue(key, out var previous) ? WeightAt(previous, now) : 0;
		_entries[key] = new(id, query, weight + 1, now);
	}

	// Each use loses half its weight in a week; using an old favorite never revives its lifetime count.
	private static double WeightAt(CommandUsage entry, long now) =>
		entry.Weight * Math.Pow(0.5, Math.Max(0, (double)now - entry.LastUsed) / HalfLifeMilliseconds);

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		_entries.Clear();
		if (text is null) return;
		var entries = JsonSerializer.Deserialize(text, UsageJson.Default.CommandUsageArray)
			?? throw new JsonException("Expected command usage entries.");
		foreach (var entry in entries) {
			if (string.IsNullOrWhiteSpace(entry.Id) || entry.Query is null || !double.IsFinite(entry.Weight) || entry.Weight < 0)
				throw new JsonException("Invalid command usage entry.");
			_entries[(entry.Id, entry.Query)] = entry;
		}
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize([.. _entries.Values], UsageJson.Default.CommandUsageArray);

	/// <inheritdoc/>
	protected override void OnUnusable(string? text, Exception cause) => throw new IOException("Could not load command learning.", cause);

	/// <inheritdoc/>
	protected override void OnPersistFailed(Exception cause) => throw new IOException("Could not save command learning.", cause);

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(CommandUsage[]))]
	private sealed partial class UsageJson : JsonSerializerContext;
}
