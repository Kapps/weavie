using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Worktrees;

/// <summary>
/// The per-workspace record of every git worktree Weavie created, persisted to
/// <c>~/.weavie/workspaces/&lt;id&gt;/worktrees.json</c> — the backbone of the "no leaked worktrees" guarantee
/// that <see cref="WorktreeManager"/> reconciles against <c>git worktree list</c>. Atomic writes; a malformed
/// file is backed up to <c>worktrees.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class WorktreeRegistry {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<WorktreeRecord> _items;

	/// <summary>Creates the registry over <paramref name="path"/>, loading it now.</summary>
	public WorktreeRegistry(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			_items = LoadLocked();
		}
	}

	/// <summary>Raised (off the UI thread) after the registry changes, so a worktree view can refresh.</summary>
	public event Action? Changed;

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this registry.</summary>
	public string FilePath { get; }

	/// <summary>Snapshot of the recorded worktrees. Safe to enumerate.</summary>
	public IReadOnlyList<WorktreeRecord> Items {
		get {
			lock (_gate) {
				return [.. _items];
			}
		}
	}

	/// <summary>Records <paramref name="record"/>, replacing any existing entry for the same path.</summary>
	public void Add(WorktreeRecord record) {
		ArgumentNullException.ThrowIfNull(record);
		lock (_gate) {
			_items.RemoveAll(r => PathsEqual(r.Path, record.Path));
			_items.Add(record);
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Drops the entry for <paramref name="path"/> (a worktree that was removed).</summary>
	public void Remove(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		bool removed;
		lock (_gate) {
			removed = _items.RemoveAll(r => PathsEqual(r.Path, path)) > 0;
			if (removed) {
				PersistLocked();
			}
		}

		if (removed) {
			Changed?.Invoke();
		}
	}

	/// <summary>The recorded entry for <paramref name="path"/>, or <c>null</c>.</summary>
	public WorktreeRecord? FindByPath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			return _items.FirstOrDefault(r => PathsEqual(r.Path, path));
		}
	}

	/// <summary>The recorded entry on <paramref name="branch"/>, or <c>null</c>.</summary>
	public WorktreeRecord? FindByBranch(string branch) {
		ArgumentException.ThrowIfNullOrEmpty(branch);
		lock (_gate) {
			return _items.FirstOrDefault(r => string.Equals(r.Branch, branch, StringComparison.Ordinal));
		}
	}

	private static bool PathsEqual(string a, string b) =>
		string.Equals(
			Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private List<WorktreeRecord> LoadLocked() =>
		JsonStoreFile.Load<List<WorktreeRecord>>(
			_fileSystem,
			FilePath,
			text => {
				var document = JsonSerializer.Deserialize<WorktreesDocument>(text);
				if (document?.Version != 2 || document.Worktrees is not { } entries) {
					throw new JsonException("Worktree document requires version 2 and a worktrees array.");
				}

				return [.. entries.Select(ParseEntry)];
			},
			static () => [],
			Log);

	private void PersistLocked() {
		var document = new WorktreesDocument {
			Version = 2,
			Worktrees = [.. _items.Select(r => new WorktreeEntry {
				Branch = r.Branch,
				Path = r.Path,
				BaseRef = r.BaseRef,
				CreatedAt = r.CreatedAtUtc,
				AgentProviderId = r.AgentProviderId,
			})],
		};
		JsonStoreFile.Persist(
			_fileSystem,
			FilePath,
			JsonSerializer.Serialize(document, JsonOptions),
			Log);
	}

	private static WorktreeRecord ParseEntry(WorktreeEntry entry) {
		if (string.IsNullOrWhiteSpace(entry.Branch)
			|| string.IsNullOrWhiteSpace(entry.Path)
			|| string.IsNullOrWhiteSpace(entry.BaseRef)
			|| string.IsNullOrWhiteSpace(entry.AgentProviderId)) {
			throw new JsonException("Worktree entries require branch, path, baseRef, and agentProviderId.");
		}
		return new WorktreeRecord {
			Branch = entry.Branch,
			Path = entry.Path,
			BaseRef = entry.BaseRef,
			CreatedAtUtc = entry.CreatedAt,
			AgentProviderId = entry.AgentProviderId,
		};
	}

	private sealed class WorktreesDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("worktrees")]
		public List<WorktreeEntry> Worktrees { get; set; } = [];
	}

	private sealed class WorktreeEntry {
		[JsonPropertyName("branch")]
		public string Branch { get; set; } = string.Empty;

		[JsonPropertyName("path")]
		public string Path { get; set; } = string.Empty;

		[JsonPropertyName("baseRef")]
		public string BaseRef { get; set; } = string.Empty;

		[JsonPropertyName("createdAt")]
		public DateTimeOffset CreatedAt { get; set; }

		[JsonPropertyName("agentProviderId")]
		public string? AgentProviderId { get; set; }
	}
}
