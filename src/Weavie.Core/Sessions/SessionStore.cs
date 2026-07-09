using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The per-workspace overlay of which sessions were loaded and which was active, persisted atomically to
/// <c>~/.weavie/workspaces/&lt;id&gt;/sessions.json</c> so a reopen (including a worker auto-update restart) comes
/// back with the same sessions loaded and the same one active. The worktree set itself is reconciled from git;
/// this store only carries the loaded/active overlay plus the last real shell-terminal size (so a restored
/// pre-spawn matches the reattaching xterm's width). A malformed file is backed up to <c>sessions.json.bad</c>
/// and reset rather than throwing.
/// </summary>
public sealed class SessionStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private List<SessionDescriptor> _items;
	private SessionId? _activeId;
	// Last real shell-terminal size (fitted, active-pane term-resize); 0 = never recorded. See ShellSize.
	private int _shellCols;
	private int _shellRows;

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

	/// <summary>
	/// The last real shell-terminal size (from a fitted, active pane), or <c>null</c> if none was recorded. A
	/// restored session seeds its shell child with this so the pre-spawn width matches the reattaching xterm —
	/// otherwise the raw scrollback replay, laid out at the placeholder 80×24, stacks garbled at the real width.
	/// </summary>
	public (int Cols, int Rows)? ShellSize {
		get {
			lock (_gate) {
				return _shellCols > 0 && _shellRows > 0 ? (_shellCols, _shellRows) : null;
			}
		}
	}

	/// <summary>
	/// Records the shell terminal's latest real size in memory, persisted by the next <see cref="Save"/> or
	/// <see cref="Flush"/> — not written per call, so a window-drag's resize storm doesn't thrash the file.
	/// </summary>
	public void RecordShellSize(int columns, int rows) {
		lock (_gate) {
			_shellCols = columns;
			_shellRows = rows;
		}
	}

	/// <summary>Persists the current overlay (including the latest recorded shell size) without replacing it —
	/// called at the graceful pre-restart / shutdown points so a resize since the last <see cref="Save"/> survives.</summary>
	public void Flush() {
		lock (_gate) {
			PersistLocked();
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

			_shellCols = document.ShellCols;
			_shellRows = document.ShellRows;

			return [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.WorktreePath))
				.Select(e => new SessionDescriptor {
					Id = new SessionId(e.Id),
					Label = e.Label,
					WorktreePath = e.WorktreePath,
					IsPrimary = e.IsPrimary,
					Loaded = e.Loaded,
					AgentProviderId = string.IsNullOrWhiteSpace(e.AgentProviderId) ? "claude" : e.AgentProviderId,
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
				ShellCols = _shellCols,
				ShellRows = _shellRows,
				Sessions = [.. _items.Select(s => new SessionEntry {
					Id = s.Id.Value,
					Label = s.Label,
					WorktreePath = s.WorktreePath,
					IsPrimary = s.IsPrimary,
					Loaded = s.Loaded,
					AgentProviderId = s.AgentProviderId,
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

		[JsonPropertyName("shellCols")]
		public int ShellCols { get; set; }

		[JsonPropertyName("shellRows")]
		public int ShellRows { get; set; }

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

		[JsonPropertyName("agentProviderId")]
		public string AgentProviderId { get; set; } = "claude";
	}
}
