using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Review.Hints;

// Records: `hint:<id>` per hint, `source:<sha256>` per captured file text (shared, dropped once unreferenced), and
// `metadata` (next id).
public sealed partial class ReviewHintStore {
	private const string MetadataKey = "metadata";
	private readonly IReviewPersistence _persistence;
	private IReadOnlyDictionary<string, string> _sources = new Dictionary<string, string>(StringComparer.Ordinal);
	private long _nextId = 1;

	// Saves first, so a failed write leaves memory matching the last committed state.
	private void Commit(IReadOnlyList<ReviewHint> hints, IReadOnlyDictionary<string, string> captured, long nextId) {
		var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
		var kept = hints.Select(hint => hint.Id).ToHashSet();
		foreach (var hint in _hints.Where(hint => !kept.Contains(hint.Id))) changes[HintKey(hint.Id)] = null;
		var known = _hints.Select(hint => hint.Id).ToHashSet();
		foreach (var hint in hints.Where(hint => !known.Contains(hint.Id))) {
			changes[HintKey(hint.Id)] = JsonSerializer.Serialize(hint, ReviewHintJson.Default.ReviewHint);
		}
		var referenced = hints.Select(hint => hint.SourceHash).ToHashSet(StringComparer.Ordinal);
		var sources = _sources.Concat(captured.Where(source => !_sources.ContainsKey(source.Key)))
			.Where(source => referenced.Contains(source.Key))
			.ToDictionary(StringComparer.Ordinal);
		foreach (string hash in _sources.Keys.Where(hash => !sources.ContainsKey(hash))) changes[SourceKey(hash)] = null;
		foreach (var (hash, text) in sources.Where(source => !_sources.ContainsKey(source.Key))) changes[SourceKey(hash)] = text;
		changes[MetadataKey] = JsonSerializer.Serialize(new HintMetadata(nextId), ReviewHintJson.Default.HintMetadata);
		_persistence.Save(changes);
		_hints = hints;
		_sources = sources;
		_nextId = nextId;
		_published(hints);
	}

	// Hints are advisory: unreadable saved hints are discarded and reported rather than failing the session.
	private void Restore() {
		IReadOnlyDictionary<string, string> records = new Dictionary<string, string>();
		try {
			records = _persistence.Read();
			if (records.Count > 0) Load(records);
		} catch (Exception error) when (error is IOException or JsonException or ArgumentException) {
			string reason = error.Message;
			try {
				_persistence.Save(records.Keys.ToDictionary(key => key, string? (_) => null, StringComparer.Ordinal));
			} catch (IOException clear) {
				reason += $" Clearing them failed too: {clear.Message}";
			}
			_rejected($"Saved review hints couldn't be read and were discarded: {reason}");
		}
	}

	private void Load(IReadOnlyDictionary<string, string> records) {
		var metadata = records.TryGetValue(MetadataKey, out string? saved)
			? Deserialize(saved, ReviewHintJson.Default.HintMetadata)
			: throw new JsonException("Review hint metadata is missing.");
		var hints = new List<ReviewHint>();
		var sources = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, value) in records) {
			if (key == MetadataKey) continue;
			if (key.StartsWith("hint:", StringComparison.Ordinal)) {
				var hint = Deserialize(value, ReviewHintJson.Default.ReviewHint);
				if (key != HintKey(hint.Id) || hint.Id >= metadata.NextId || !ValidHint(hint)) {
					throw new JsonException("Invalid review hint.");
				}
				hints.Add(hint);
			} else if (key.StartsWith("source:", StringComparison.Ordinal) && key == SourceKey(Hash(value))) {
				sources.Add(Hash(value), value);
			} else {
				throw new JsonException("Unknown review hint record.");
			}
		}
		if (hints.Any(hint => !sources.ContainsKey(hint.SourceHash))
			|| sources.Keys.Any(hash => hints.All(hint => hint.SourceHash != hash))) {
			throw new JsonException("Review hint sources do not match their hints.");
		}
		_hints = [.. hints.OrderBy(hint => hint.Id)];
		_sources = sources;
		_nextId = metadata.NextId;
	}

	private bool ValidHint(ReviewHint hint) =>
		hint.Id > 0 && hint.Line > 0 && Enum.IsDefined(hint.Severity)
		&& hint.Path is { Length: > 0 } && !Path.IsPathRooted(hint.Path)
		&& PathBoundary.Contains(_root, Path.Combine(_root, hint.Path))
		&& hint.Title is { Length: > 0 } && hint.Body is not null && hint.Agent is { Length: > 0 }
		&& hint.SourceHash is not null && hint.Quote is not null;

	private static string HintKey(long id) => "hint:" + id.ToString(CultureInfo.InvariantCulture);

	private static string SourceKey(string hash) => "source:" + hash;

	private static T Deserialize<T>(string value, JsonTypeInfo<T> type) =>
		JsonSerializer.Deserialize(value, type) ?? throw new JsonException("Empty review hint record.");

	internal sealed record HintMetadata(long NextId);
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReviewHint))]
[JsonSerializable(typeof(ReviewHintStore.HintMetadata))]
internal sealed partial class ReviewHintJson : JsonSerializerContext;
