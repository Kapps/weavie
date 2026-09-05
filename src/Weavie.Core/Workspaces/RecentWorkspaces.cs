using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>
/// The app-global most-recently-opened workspace list (most-recent first, deduped case-insensitively on
/// Windows), persisted atomically to <c>~/.weavie/recents.json</c>. A malformed file is backed up to
/// <c>recents.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class RecentWorkspaces : JsonDocumentStore {
	private const int MaxItems = 20;
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private List<string> _items = [];

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/recents.json</c>), loading it now.</summary>
	/// <param name="fileSystem">The filesystem the list persists through.</param>
	/// <param name="path">The backing file, or <c>null</c> for the default.</param>
	public RecentWorkspaces(IFileSystem fileSystem, string? path)
		: base(fileSystem, path ?? WeaviePaths.RecentsFile) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after the list changes, so menus can refresh.</summary>
	public event Action? Changed;

	/// <summary>The recent workspace root paths, most-recent first. Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<string> Items {
		get { lock (Gate) { return [.. _items]; } }
	}

	/// <summary>The most-recently-opened workspace root, or <c>null</c> when there is no history.</summary>
	public string? LastOpened {
		get { lock (Gate) { return _items.Count > 0 ? _items[0] : null; } }
	}

	/// <summary>Records <paramref name="rootPath"/> as the most-recently-opened workspace (moved to front, deduped, capped).</summary>
	public void Add(string rootPath) {
		ArgumentException.ThrowIfNullOrEmpty(rootPath);
		string full = Path.GetFullPath(rootPath);
		lock (Gate) {
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
		lock (Gate) {
			removed = _items.RemoveAll(p => PathsEqual(p, full)) > 0;
			if (removed) {
				PersistLocked();
			}
		}

		if (removed) {
			Changed?.Invoke();
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = text is null ? null : JsonSerializer.Deserialize<RecentsDocument>(text);
		_items = document?.Recents is { } recents ? [.. recents.Where(p => !string.IsNullOrWhiteSpace(p))] : [];
	}

	/// <inheritdoc/>
	protected override string Render() =>
		JsonSerializer.Serialize(new RecentsDocument { Version = 1, Recents = _items }, JsonOptions);

	private static bool PathsEqual(string a, string b) =>
		string.Equals(
			a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private sealed class RecentsDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("recents")]
		public List<string> Recents { get; set; } = [];
	}
}
