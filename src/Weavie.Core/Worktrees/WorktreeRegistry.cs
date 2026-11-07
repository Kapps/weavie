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
public sealed partial class WorktreeRegistry : JsonDocumentStore {
	private List<WorktreeRecord> _items = [];

	/// <summary>Creates the registry over <paramref name="path"/>, loading it now.</summary>
	/// <param name="fileSystem">The filesystem the registry persists through.</param>
	/// <param name="path">The backing file.</param>
	public WorktreeRegistry(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after the registry changes, so a worktree view can refresh.</summary>
	public event Action? Changed;

	/// <summary>Snapshot of the recorded worktrees. Safe to enumerate.</summary>
	public IReadOnlyList<WorktreeRecord> Items {
		get {
			lock (Gate) {
				return [.. _items];
			}
		}
	}

	/// <summary>Records <paramref name="record"/>, replacing any existing entry for the same path.</summary>
	public void Add(WorktreeRecord record) {
		ArgumentNullException.ThrowIfNull(record);
		lock (Gate) {
			_items.RemoveAll(r => PathIdentity.Equals(r.Path, record.Path));
			_items.Add(record);
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Sets each recorded worktree's branch to what git reports checked out at its path, in one save; paths with no
	/// record are skipped, so a removal racing this can't be undone.
	/// </summary>
	public void SyncBranches(IReadOnlyList<(string Path, string? Branch)> checkouts) {
		ArgumentNullException.ThrowIfNull(checkouts);
		bool changed = false;
		lock (Gate) {
			foreach (var (path, branch) in checkouts) {
				int index = _items.FindIndex(r => PathIdentity.Equals(r.Path, path));
				if (index >= 0 && _items[index].Branch != branch) {
					_items[index] = _items[index] with { Branch = branch };
					changed = true;
				}
			}

			if (changed) {
				PersistLocked();
			}
		}

		if (changed) {
			Changed?.Invoke();
		}
	}

	/// <summary>Drops the entry for <paramref name="path"/> (a worktree that was removed).</summary>
	public void Remove(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		bool removed;
		lock (Gate) {
			removed = _items.RemoveAll(r => PathIdentity.Equals(r.Path, path)) > 0;
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
		lock (Gate) {
			return _items.FirstOrDefault(r => PathIdentity.Equals(r.Path, path));
		}
	}

	/// <summary>The recorded entry on <paramref name="branch"/>, or <c>null</c>.</summary>
	public WorktreeRecord? FindByBranch(string branch) {
		ArgumentException.ThrowIfNullOrEmpty(branch);
		lock (Gate) {
			return _items.FirstOrDefault(r => string.Equals(r.Branch, branch, StringComparison.Ordinal));
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		if (text is null) {
			_items = [];
			return;
		}

		var document = JsonSerializer.Deserialize(text, WorktreeRegistryJson.Default.WorktreesDocument);
		if (document?.Version != 2 || document.Worktrees is not { } entries) {
			throw new JsonException("Worktree document requires version 2 and a worktrees array.");
		}

		_items = [.. entries.Select(ParseEntry)];
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(
		new WorktreesDocument {
			Version = 2,
			Worktrees = [.. _items.Select(r => new WorktreeEntry {
				Branch = r.Branch,
				Path = r.Path,
				CreatedAt = r.CreatedAtUtc,
				AgentProviderId = r.AgentProviderId,
			})],
		},
		WorktreeRegistryJson.Default.WorktreesDocument);


	private static WorktreeRecord ParseEntry(WorktreeEntry entry) {
		if (string.IsNullOrWhiteSpace(entry.Path)
			|| string.IsNullOrWhiteSpace(entry.AgentProviderId)) {
			throw new JsonException("Worktree entries require a path and agentProviderId.");
		}
		return new WorktreeRecord {
			Branch = entry.Branch,
			Path = entry.Path,
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
		public string? Branch { get; set; }

		[JsonPropertyName("path")]
		public string Path { get; set; } = string.Empty;

		[JsonPropertyName("createdAt")]
		public DateTimeOffset CreatedAt { get; set; }

		[JsonPropertyName("agentProviderId")]
		public string? AgentProviderId { get; set; }
	}

	[JsonSourceGenerationOptions(WriteIndented = true)]
	[JsonSerializable(typeof(WorktreesDocument))]
	private sealed partial class WorktreeRegistryJson : JsonSerializerContext;
}
