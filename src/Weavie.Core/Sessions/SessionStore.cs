using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The per-workspace overlay of which sessions were loaded and which was active, persisted atomically to
/// <c>~/.weavie/workspaces/&lt;id&gt;/sessions.json</c> so a reopen (including a worker auto-update restart) comes
/// back with the same sessions loaded and the same one active. The worktree set itself is reconciled from git;
/// this store only carries the loaded/active overlay. A malformed file is backed up to <c>sessions.json.bad</c>
/// and reset rather than throwing.
/// </summary>
public sealed class SessionStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private List<SessionDescriptor> _items;
	private SessionId? _activeId;

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	public SessionStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			_items = LoadLocked(out _activeId);
		}
	}

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Snapshot of the persisted sessions. Safe to enumerate.</summary>
	public IReadOnlyList<SessionDescriptor> Items {
		get {
			lock (_gate) {
				return [.. _items];
			}
		}
	}

	/// <summary>The session that was active when last persisted, or <c>null</c> (the primary was active).</summary>
	public SessionId? ActiveId {
		get {
			lock (_gate) {
				return _activeId;
			}
		}
	}

	/// <summary>Replaces the whole overlay with <paramref name="sessions"/> and <paramref name="activeId"/>, persisting it.</summary>
	public void Save(IReadOnlyList<SessionDescriptor> sessions, SessionId? activeId) {
		ArgumentNullException.ThrowIfNull(sessions);
		lock (_gate) {
			_items = [.. sessions];
			_activeId = activeId;
			PersistLocked();
		}
	}

	private List<SessionDescriptor> LoadLocked(out SessionId? active) {
		active = null;
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[sessions] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		try {
			var document = JsonSerializer.Deserialize<SessionsDocument>(text);
			if (document?.Sessions is not { } entries) {
				return [];
			}

			if (!string.IsNullOrWhiteSpace(document.ActiveId)) {
				active = new SessionId(document.ActiveId);
			}

			return [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.WorktreePath))
				.Select(e => new SessionDescriptor {
					Id = new SessionId(e.Id),
					Label = e.Label,
					WorktreePath = e.WorktreePath,
					IsPrimary = e.IsPrimary,
					Loaded = e.Loaded,
				})];
		} catch (JsonException ex) {
			Log?.Invoke($"[sessions] {FilePath} is malformed ({ex.Message}); backing up to sessions.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "sessions", Log);
			return [];
		}
	}

	private void PersistLocked() {
		try {
			var document = new SessionsDocument {
				Version = 1,
				ActiveId = _activeId?.Value,
				Sessions = [.. _items.Select(s => new SessionEntry {
					Id = s.Id.Value,
					Label = s.Label,
					WorktreePath = s.WorktreePath,
					IsPrimary = s.IsPrimary,
					Loaded = s.Loaded,
				})],
			};
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[sessions] could not persist session set: {ex.Message}");
		}
	}

	private sealed class SessionsDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("activeId")]
		public string? ActiveId { get; set; }

		[JsonPropertyName("sessions")]
		public List<SessionEntry> Sessions { get; set; } = [];
	}

	private sealed class SessionEntry {
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;

		[JsonPropertyName("label")]
		public string Label { get; set; } = string.Empty;

		[JsonPropertyName("worktreePath")]
		public string WorktreePath { get; set; } = string.Empty;

		[JsonPropertyName("isPrimary")]
		public bool IsPrimary { get; set; }

		[JsonPropertyName("loaded")]
		public bool Loaded { get; set; }
	}
}
