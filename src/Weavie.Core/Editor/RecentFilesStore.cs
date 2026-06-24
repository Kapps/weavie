using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>One recorded file visit: its native path, how many times it has been opened, and when it was last
/// opened (UTC ticks). Backs the frecency ranking in <see cref="RecentFilesStore"/>.</summary>
public sealed record RecentFile(string Path, int Count, long LastOpenedTicks);

/// <summary>
/// Loads, persists, and ranks the per-workspace recent-files list at
/// <c>~/.weavie/workspaces/&lt;id&gt;/recent-files.json</c>. Ranking is frecency — a file's visit count damped by a
/// recency half-life — so a file opened often <em>and</em> recently outranks a one-off open, and stale entries fade
/// without being discarded. Writes are atomic; a malformed file is backed up to <c>recent-files.json.bad</c> and
/// reset, matching <see cref="EditorSessionStore"/>'s recovery contract.
/// </summary>
public sealed class RecentFilesStore {
	// Cap on persisted entries: past this the lowest-frecency files are evicted so the file never grows unbounded.
	private const int MaxEntries = 200;
	// A file's visit weight halves this many days after its last open, so recency outweighs raw count without ever
	// fully discarding a frequently-used file.
	private const double HalfLifeDays = 3.0;

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, RecentFile> _byPath = new(StringComparer.Ordinal);

	/// <summary>Creates a store over <paramref name="filePath"/>, loading the persisted list now.</summary>
	public RecentFilesStore(IFileSystem fileSystem, string filePath) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(filePath);
		_fileSystem = fileSystem;
		FilePath = filePath;
		lock (_gate) {
			foreach (var file in LoadLocked()) {
				_byPath[file.Path] = file;
			}
		}
	}

	/// <summary>Diagnostic log line — load failures and malformed-file resets.</summary>
	public event Action<string>? Log;

	/// <summary>The recent-files file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Records a visit to <paramref name="path"/> at <paramref name="nowTicks"/> (UTC ticks), bumping its
	/// count and recency, evicting the lowest-frecency overflow, and persisting atomically.</summary>
	public void Record(string path, long nowTicks) {
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		lock (_gate) {
			int count = _byPath.TryGetValue(path, out var existing) ? existing.Count + 1 : 1;
			_byPath[path] = new RecentFile(path, count, nowTicks);
			EvictLocked(nowTicks);
			PersistLocked();
		}
	}

	/// <summary>The top <paramref name="count"/> paths by frecency at <paramref name="nowTicks"/>, most-relevant
	/// first. The caller filters to files that still exist / are in the active index.</summary>
	public IReadOnlyList<string> Top(int count, long nowTicks) {
		lock (_gate) {
			return _byPath.Values
				.OrderByDescending(file => Score(file, nowTicks))
				.Take(count)
				.Select(file => file.Path)
				.ToList();
		}
	}

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

	private IReadOnlyList<RecentFile> LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[recent-files] could not read {FilePath}: {ex.Message}; using empty list");
			return [];
		}

		try {
			var parsed = JsonSerializer.Deserialize<PersistModel>(text, JsonOptions);
			if (parsed?.Files is { } files) {
				return [.. files.Where(file => !string.IsNullOrEmpty(file.Path))];
			}
		} catch (JsonException) {
			// fall through to the reset path
		}

		Log?.Invoke($"[recent-files] {FilePath} is malformed; backing up to recent-files.json.bad and resetting");
		JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "recent-files", Log);
		return [];
	}

	private void PersistLocked() {
		try {
			var model = new PersistModel([.. _byPath.Values]);
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(model, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[recent-files] could not persist: {ex.Message}");
		}
	}

	private static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private sealed record PersistModel(IReadOnlyList<RecentFile> Files);
}
