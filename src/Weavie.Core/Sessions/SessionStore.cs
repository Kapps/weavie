using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The per-workspace set of sessions (and which one was active), persisted to
/// <c>~/.weavie/workspaces/&lt;id&gt;/sessions.json</c> so a workspace reopens with the same sessions bound
/// to the same worktrees. Atomic writes; a malformed file is backed up to <c>sessions.json.bad</c> and
/// reset rather than throwing.
/// </summary>
public sealed class SessionStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<SessionDescriptor> _items;
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

	/// <summary>Raised (off the UI thread) after the session set or active session changes.</summary>
	public event Action? Changed;

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

	/// <summary>The session that was active when last persisted, or <c>null</c>.</summary>
	public SessionId? ActiveId {
		get {
			lock (_gate) {
				return _activeId;
			}
		}
	}

	/// <summary>Adds or replaces (by id) <paramref name="descriptor"/>.</summary>
	public void Add(SessionDescriptor descriptor) {
		ArgumentNullException.ThrowIfNull(descriptor);
		lock (_gate) {
			_items.RemoveAll(s => s.Id == descriptor.Id);
			_items.Add(descriptor);
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Removes the session <paramref name="id"/> (clearing the active pointer if it referred to it).</summary>
	public void Remove(SessionId id) {
		bool removed;
		lock (_gate) {
			removed = _items.RemoveAll(s => s.Id == id) > 0;
			if (_activeId == id) {
				_activeId = null;
			}

			if (removed) {
				PersistLocked();
			}
		}

		if (removed) {
			Changed?.Invoke();
		}
	}

	/// <summary>Records <paramref name="id"/> as the active session.</summary>
	public void SetActive(SessionId id) {
		bool changed;
		lock (_gate) {
			changed = _activeId != id;
			if (changed) {
				_activeId = id;
				PersistLocked();
			}
		}

		if (changed) {
			Changed?.Invoke();
		}
	}

	/// <summary>The descriptor for <paramref name="id"/>, or <c>null</c>.</summary>
	public SessionDescriptor? Get(SessionId id) {
		lock (_gate) {
			return _items.FirstOrDefault(s => s.Id == id);
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
				})];
		} catch (JsonException ex) {
			Log?.Invoke($"[sessions] {FilePath} is malformed ({ex.Message}); backing up to sessions.json.bad and resetting");
			BackupBadFileLocked(text);
			return [];
		}
	}

	private void BackupBadFileLocked(string text) {
		try {
			_fileSystem.WriteAllText(FilePath + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[sessions] could not back up malformed session set: {ex.Message}");
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
	}
}
