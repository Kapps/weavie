using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>
/// The app-global most-recently-opened workspace list, persisted to <c>~/.weavie/recents.json</c>.
/// Most-recent first; opening a workspace moves it to the front and dedupes (case-insensitively on Windows).
/// Used to reopen the last workspace on launch and to populate the Open Recent menu. Atomic writes; a
/// malformed file is backed up to <c>recents.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class RecentWorkspaces {
	private const int MaxItems = 20;
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<string> _items;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/recents.json</c>), loading it now.</summary>
	public RecentWorkspaces(IFileSystem fileSystem, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
		FilePath = path ?? WeaviePaths.RecentsFile;
		lock (_gate) {
			_items = LoadLocked();
		}
	}

	/// <summary>Raised (off the UI thread) after the list changes, so menus can refresh.</summary>
	public event Action? Changed;

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The recents file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The recent workspace root paths, most-recent first. Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<string> Items {
		get { lock (_gate) { return [.. _items]; } }
	}

	/// <summary>The most-recently-opened workspace root, or <c>null</c> when there is no history.</summary>
	public string? LastOpened {
		get { lock (_gate) { return _items.Count > 0 ? _items[0] : null; } }
	}

	/// <summary>Records <paramref name="rootPath"/> as the most-recently-opened workspace (moved to front, deduped, capped).</summary>
	public void Add(string rootPath) {
		ArgumentException.ThrowIfNullOrEmpty(rootPath);
		string full = Path.GetFullPath(rootPath);
		lock (_gate) {
			_items.RemoveAll(p => PathsEqual(p, full));
			_items.Insert(0, full);
			if (_items.Count > MaxItems) {
				_items.RemoveRange(MaxItems, _items.Count - MaxItems);
			}

			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Drops <paramref name="rootPath"/> from the list (e.g. a folder that no longer exists).</summary>
	public void Remove(string rootPath) {
		ArgumentException.ThrowIfNullOrEmpty(rootPath);
		string full = Path.GetFullPath(rootPath);
		bool removed;
		lock (_gate) {
			removed = _items.RemoveAll(p => PathsEqual(p, full)) > 0;
			if (removed) {
				PersistLocked();
			}
		}

		if (removed) {
			Changed?.Invoke();
		}
	}

	private static bool PathsEqual(string a, string b) =>
		string.Equals(
			a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private List<string> LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[recents] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		try {
			var document = JsonSerializer.Deserialize<RecentsDocument>(text);
			return document?.Recents is { } recents
				? [.. recents.Where(p => !string.IsNullOrWhiteSpace(p))]
				: [];
		} catch (JsonException ex) {
			Log?.Invoke($"[recents] {FilePath} is malformed ({ex.Message}); backing up to recents.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "recents", Log);
			return [];
		}
	}

	private void PersistLocked() {
		try {
			string json = JsonSerializer.Serialize(new RecentsDocument { Version = 1, Recents = _items }, JsonOptions);
			_fileSystem.WriteAllTextAtomic(FilePath, json);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[recents] could not persist recents: {ex.Message}");
		}
	}

	private sealed class RecentsDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("recents")]
		public List<string> Recents { get; set; } = [];
	}
}
