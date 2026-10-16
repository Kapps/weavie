using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private const string TextPrefix = "text:";
	private readonly ReviewJson _json = new();
	private readonly TextStore _storedTexts = new();

	private StoredFile Store(string path, TextChanges texts) {
		var state = Capture(path, withDisk: false);
		return new(path, texts.Add(path, state.Baseline), state.BaselineExists, texts.Add(path, state.Current), state.CurrentExists,
			texts.Add(path, state.ReviewBaseline), state.ReviewBaselineExists, texts.Add(path, state.AcceptedAnchor),
			state.AcceptedAnchorExists, texts.Add(path, state.PreEdit), state.Provenance is { } provenance
				? new StoredProvenance(texts.Add(path, provenance.Text), provenance.Lines, provenance.DeletedAtGap) : null);
	}

	// A file's boundaries reference content-addressed text records, so a save rewrites only the texts that changed.
	private sealed record StoredFile(string Path, string Baseline, bool BaselineExists, string Current, bool CurrentExists,
		string ReviewBaseline, bool ReviewBaselineExists, string AcceptedAnchor, bool AcceptedAnchorExists, string PreEdit,
		StoredProvenance? Provenance) {
		public HashSet<string> Hashes() => [.. new[] { Baseline, Current, ReviewBaseline, AcceptedAnchor, PreEdit, Provenance?.Text }.OfType<string>()];

		public PathState Restore(IReadOnlyDictionary<string, string> texts) {
			string Text(string hash) => hash is not null && texts.TryGetValue(hash, out string? text)
				? text : throw new JsonException("Invalid review text reference.");
			return new(Path, true, Text(Baseline), BaselineExists, Text(Current), CurrentExists,
				Text(ReviewBaseline), ReviewBaselineExists, Text(AcceptedAnchor), AcceptedAnchorExists, Text(PreEdit),
				Provenance is { } provenance ? new ProvenanceFile {
					Text = Text(provenance.Text),
					Lines = provenance.Lines,
					DeletedAtGap = provenance.DeletedAtGap
				} : null,
				false, string.Empty);
		}
	}

	private sealed record StoredProvenance(string Text, [property: JsonConverter(typeof(OriginRunsConverter))] ImmutableArray<AgentOrigin?> Lines,
		ImmutableDictionary<int, ImmutableList<DeletedSegment>> DeletedAtGap);

	// Committed text references per file; a text record lives exactly as long as some file references it.
	private sealed class TextStore {
		private readonly Dictionary<string, int> _references = new(StringComparer.Ordinal);
		private readonly Dictionary<string, HashSet<string>> _files = new(PathIdentity.Comparer);
		private readonly ConditionalWeakTable<string, string> _hashes = [];

		public string Hash(string text) =>
			_hashes.GetValue(text, static value => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(value.AsSpan()))));
		public int References(string hash) => _references.GetValueOrDefault(hash);
		public bool Referenced(string hash) => _references.ContainsKey(hash);
		public HashSet<string> Hashes(string path) => _files.GetValueOrDefault(path, []);

		public void Reference(string path, HashSet<string> hashes, IReadOnlyDictionary<string, string> texts) {
			foreach (string hash in hashes) _hashes.AddOrUpdate(texts[hash], hash);
			Set(path, hashes);
		}

		public void Set(string path, HashSet<string> hashes) {
			foreach (string hash in Hashes(path))
				if (--_references[hash] == 0) _references.Remove(hash);
			foreach (string hash in hashes) _references[hash] = References(hash) + 1;
			if (hashes.Count == 0) _files.Remove(path);
			else _files[path] = hashes;
		}
	}

	// One checkpoint's text references, committed to the store only after the save succeeds.
	private sealed class TextChanges(TextStore store) {
		private readonly Dictionary<string, HashSet<string>> _files = new(PathIdentity.Comparer);
		private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

		public string Add(string path, string text) {
			string hash = store.Hash(text);
			_texts[hash] = text;
			Files(path).Add(hash);
			return hash;
		}

		public string? Release(string path) {
			Files(path);
			return null;
		}

		public void Collect(Dictionary<string, string?> changes) {
			var deltas = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var (path, hashes) in _files) {
				foreach (string hash in store.Hashes(path)) deltas[hash] = deltas.GetValueOrDefault(hash) - 1;
				foreach (string hash in hashes) deltas[hash] = deltas.GetValueOrDefault(hash) + 1;
			}
			foreach (var (hash, delta) in deltas) {
				int before = store.References(hash);
				if (before == 0 && delta > 0) changes[TextPrefix + hash] = _texts[hash];
				else if (before > 0 && before + delta == 0) changes[TextPrefix + hash] = null;
			}
		}

		public void Commit() {
			foreach (var (path, hashes) in _files) store.Set(path, hashes);
		}

		private HashSet<string> Files(string path) {
			if (!_files.TryGetValue(path, out var hashes)) _files[path] = hashes = new(StringComparer.Ordinal);
			return hashes;
		}
	}

	// Serializes one review record; within it an origin's prompt is written once and later occurrences share it.
	private sealed class ReviewJson {
		private readonly Dictionary<long, string?> _prompts = [];

		public ReviewJson() {
			Types = new(new JsonSerializerOptions { Converters = { new OriginConverter(_prompts) } });
		}

		public ChangeTrackerJson Types { get; }

		public string Serialize<T>(T value, JsonTypeInfo<T> type) {
			_prompts.Clear();
			return JsonSerializer.Serialize(value, type);
		}

		public T Deserialize<T>(string value, JsonTypeInfo<T> type) =>
			DeserializeNullable(value, type) ?? throw new JsonException("Empty review record.");

		public T? DeserializeNullable<T>(string value, JsonTypeInfo<T> type) {
			_prompts.Clear();
			return JsonSerializer.Deserialize(value, type);
		}
	}

	private sealed class OriginConverter(Dictionary<long, string?> prompts) : JsonConverter<AgentOrigin> {
		public override AgentOrigin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
			var stored = JsonSerializer.Deserialize(ref reader, StoredOriginType(options)) ?? throw new JsonException("Missing origin.");
			string? prompt = stored.Prompt;
			if (!stored.Shared) prompts[stored.Id] = prompt;
			else if (prompt is not null || !prompts.TryGetValue(stored.Id, out prompt)) throw new JsonException("Unknown shared origin.");
			return new(prompt, stored.Pending, stored.Id);
		}

		public override void Write(Utf8JsonWriter writer, AgentOrigin value, JsonSerializerOptions options) {
			bool shared = !prompts.TryAdd(value.Id, value.Prompt);
			JsonSerializer.Serialize(writer, new StoredOrigin(value.Id, value.Pending, shared ? null : value.Prompt, shared), StoredOriginType(options));
		}

		private static JsonTypeInfo<StoredOrigin> StoredOriginType(JsonSerializerOptions options) =>
			(JsonTypeInfo<StoredOrigin>)options.GetTypeInfo(typeof(StoredOrigin));
	}

	private sealed record StoredOrigin(long Id, bool Pending, string? Prompt, bool Shared);

	// Consecutive lines share an origin, so each run stores its origin once rather than once per line.
	private sealed class OriginRunsConverter : JsonConverter<ImmutableArray<AgentOrigin?>> {
		public override ImmutableArray<AgentOrigin?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
			var lines = ImmutableArray.CreateBuilder<AgentOrigin?>();
			foreach (var run in JsonSerializer.Deserialize(ref reader, RunsType(options)) ?? throw new JsonException("Missing origin runs.")) {
				if (run is null || run.Count <= 0 || run.Count > Array.MaxLength - lines.Count) throw new JsonException("Invalid origin run.");
				lines.AddRange(Enumerable.Repeat(run.Origin, run.Count));
			}
			return lines.ToImmutable();
		}

		public override void Write(Utf8JsonWriter writer, ImmutableArray<AgentOrigin?> value, JsonSerializerOptions options) {
			List<OriginRun> runs = [];
			foreach (var origin in value) {
				if (runs.Count > 0 && Equals(runs[^1].Origin, origin)) runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
				else runs.Add(new(origin, 1));
			}
			JsonSerializer.Serialize(writer, runs, RunsType(options));
		}

		private static JsonTypeInfo<List<OriginRun>> RunsType(JsonSerializerOptions options) =>
			(JsonTypeInfo<List<OriginRun>>)options.GetTypeInfo(typeof(List<OriginRun>));
	}

	private sealed record OriginRun(AgentOrigin? Origin, int Count);

	private sealed record ReviewMetadata(string Root, ReviewContext? Review, long NextOriginId, long NextActionId, long NextHistoryId);

	[JsonSerializable(typeof(StoredFile))]
	[JsonSerializable(typeof(StoredOrigin))]
	[JsonSerializable(typeof(List<OriginRun>))]
	[JsonSerializable(typeof(StoredHistory))]
	[JsonSerializable(typeof(StoredPatches))]
	[JsonSerializable(typeof(ReviewMetadata))]
	[JsonSerializable(typeof(string))]
	private sealed partial class ChangeTrackerJson : JsonSerializerContext;
}
