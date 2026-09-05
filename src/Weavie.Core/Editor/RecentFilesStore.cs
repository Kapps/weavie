using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>One recorded file visit: its workspace-relative path, how many times it has been opened, and when it was last
/// opened (UTC ticks). Backs the frecency ranking in <see cref="RecentFilesStore"/>.</summary>
public sealed record RecentFile(string Path, int Count, long LastOpenedTicks);

/// <summary>
/// Loads, persists, and ranks the per-workspace recent-files list at
/// <c>~/.weavie/workspaces/&lt;id&gt;/recent-files.json</c>. Ranking is frecency — a file's visit count damped by a
/// recency half-life — so a file opened often <em>and</em> recently outranks a one-off open, and stale entries fade
/// without being discarded. Writes are atomic; a malformed file is backed up to <c>recent-files.json.bad</c> and
/// reset.
/// </summary>
public sealed class RecentFilesStore : JsonDocumentStore {
	// Cap on persisted entries: past this the lowest-frecency files are evicted so the file never grows unbounded.
	private const int MaxEntries = 200;
	// A file's visit weight halves this many days after its last open, so recency outweighs raw count without ever
	// fully discarding a frequently-used file.
	private const double HalfLifeDays = 3.0;

	private static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly Dictionary<string, RecentFile> _byPath = new(StringComparer.Ordinal);

	/// <summary>Creates a store over <paramref name="filePath"/>, loading the persisted list now.</summary>
	/// <param name="fileSystem">The filesystem the list persists through.</param>
	/// <param name="filePath">The backing file.</param>
	public RecentFilesStore(IFileSystem fileSystem, string filePath) : base(fileSystem, filePath) {
		Load();
	}

	/// <summary>Records a visit to <paramref name="path"/> at <paramref name="nowTicks"/> (UTC ticks), bumping its
	/// count and recency, evicting the lowest-frecency overflow, and persisting atomically.</summary>
	public void Record(string path, long nowTicks) {
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		lock (Gate) {
			int count = _byPath.TryGetValue(path, out var existing) ? existing.Count + 1 : 1;
			_byPath[path] = new RecentFile(path, count, nowTicks);
			EvictLocked(nowTicks);
			PersistLocked();
		}
	}

	/// <summary>The top <paramref name="count"/> paths by frecency at <paramref name="nowTicks"/>, most-relevant
	/// first. The caller filters to files that still exist / are in the active index.</summary>
	public IReadOnlyList<string> Top(int count, long nowTicks) {
		lock (Gate) {
			return _byPath.Values
				.OrderByDescending(file => Score(file, nowTicks))
				.Take(count)
				.Select(file => file.Path)
				.ToList();
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		_byPath.Clear();
		if (text is null) {
			return;
		}

		var parsed = JsonSerializer.Deserialize<PersistModel>(text, JsonOptions);
		if (parsed is not { Version: 2, Files: { } files }) {
			throw new JsonException("The document is not a version 2 recent-files list.");
		}

		foreach (var file in files.Where(file => !string.IsNullOrEmpty(file.Path))) {
			_byPath[file.Path] = file;
		}
	}

	/// <inheritdoc/>
	protected override string Render() =>
		JsonSerializer.Serialize(new PersistModel(2, [.. _byPath.Values]), JsonOptions);

	// count * 0.5^(ageDays / halfLife): visit frequency, halved every HalfLifeDays since the last open.
	private static double Score(RecentFile file, long nowTicks) {
		double ageDays = Math.Max(0, nowTicks - file.LastOpenedTicks) / (double)TimeSpan.TicksPerDay;
		return file.Count * Math.Pow(0.5, ageDays / HalfLifeDays);
	}

	private void EvictLocked(long nowTicks) {
		if (_byPath.Count <= MaxEntries) {
			return;
		}

		foreach (string path in _byPath.Values
			.OrderByDescending(file => Score(file, nowTicks))
			.Skip(MaxEntries)
			.Select(file => file.Path)
			.ToList()) {
			_byPath.Remove(path);
		}
	}

	private sealed record PersistModel(int Version, IReadOnlyList<RecentFile> Files);
}
