using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The per-workspace session overlay, persisted atomically to
/// <c>~/.weavie/workspaces/&lt;id&gt;/sessions.json</c> so a reopen (including a worker auto-update restart) comes
/// back with each slot's provider, loaded state, and editor state. Selection is client-owned. Git
/// remains authoritative for the worktree set. The store also carries the last real shell-terminal size so a
/// restored pre-spawn matches the reattaching xterm's width. A malformed file is backed up to
/// <c>sessions.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class SessionStore {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private List<SessionDescriptor> _items;
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
			_items = LoadLocked();
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

	/// <summary>Replaces the whole loaded-session overlay and persists it.</summary>
	public void Save(IReadOnlyList<SessionDescriptor> sessions) {
		ArgumentNullException.ThrowIfNull(sessions);
		lock (_gate) {
			_items = [.. sessions];
			PersistLocked();
		}
	}

	/// <summary>Strictly reads one session document without repairing or writing to its source.</summary>
	public static SessionStoreSnapshot ReadSnapshot(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		if (!fileSystem.FileExists(path)) {
			throw new FileNotFoundException("The session snapshot does not exist.", path);
		}
		return ParseSnapshot(fileSystem.ReadAllText(path));
	}

	/// <summary>Atomically writes a complete, already-projected session snapshot.</summary>
	public static void WriteSnapshot(IFileSystem fileSystem, string path, SessionStoreSnapshot snapshot) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(snapshot);
		fileSystem.WriteAllTextAtomic(path, SerializeSnapshot(snapshot));
	}

	private List<SessionDescriptor> LoadLocked() =>
		JsonStoreFile.Load<List<SessionDescriptor>>(
			_fileSystem,
			FilePath,
			text => {
				var snapshot = ParseSnapshot(text);
				_shellCols = snapshot.ShellColumns;
				_shellRows = snapshot.ShellRows;
				return [.. snapshot.Items];
			},
			static () => [],
			Log);

	private static SessionStoreSnapshot ParseSnapshot(string text) {
		var document = JsonSerializer.Deserialize<SessionsDocument>(text, JsonOptions)
			?? throw new JsonException("Session document was empty.");
		if (document.Version != 3) {
			throw new JsonException($"Unsupported session document version {document.Version}.");
		}
		var entries = document.Sessions
			?? throw new JsonException("Session document has no sessions array.");
		return new SessionStoreSnapshot {
			Items = [.. entries.Select(ParseEntry)],
			ShellColumns = document.ShellCols,
			ShellRows = document.ShellRows,
		};
	}

	private static SessionDescriptor ParseEntry(SessionEntry? entry) {
		if (entry is null
			|| string.IsNullOrWhiteSpace(entry.Id)
			|| string.IsNullOrWhiteSpace(entry.Label)
			|| string.IsNullOrWhiteSpace(entry.WorktreePath)
			|| string.IsNullOrWhiteSpace(entry.AgentProviderId)
			|| entry.Loaded is not { } loaded
			|| entry.EditorSession is not { Open: not null } editorSession) {
			throw new JsonException("Session entry is missing required version 3 data.");
		}

		return new SessionDescriptor {
			Id = new SessionId(entry.Id),
			Label = entry.Label,
			WorktreePath = entry.WorktreePath,
			Loaded = loaded,
			AgentProviderId = entry.AgentProviderId,
			EditorSession = editorSession,
		};
	}

	private void PersistLocked() {
		JsonStoreFile.Persist(
			_fileSystem,
			FilePath,
			SerializeSnapshot(new SessionStoreSnapshot {
				Items = _items,
				ShellColumns = _shellCols,
				ShellRows = _shellRows,
			}),
			Log);
	}

	private static string SerializeSnapshot(SessionStoreSnapshot snapshot) =>
		JsonSerializer.Serialize(new SessionsDocument {
			Version = 3,
			ShellCols = snapshot.ShellColumns,
			ShellRows = snapshot.ShellRows,
			Sessions = [.. snapshot.Items.Select(ToEntry)],
		}, JsonOptions);

	private static SessionEntry ToEntry(SessionDescriptor session) => new() {
		Id = session.Id.Value,
		Label = session.Label,
		WorktreePath = session.WorktreePath,
		Loaded = session.Loaded,
		AgentProviderId = session.AgentProviderId,
		EditorSession = session.EditorSession,
	};

	private sealed class SessionsDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("shellCols")]
		public int ShellCols { get; set; }

		[JsonPropertyName("shellRows")]
		public int ShellRows { get; set; }

		[JsonPropertyName("sessions")]
		public List<SessionEntry?>? Sessions { get; set; }
	}

	private sealed class SessionEntry {
		[JsonPropertyName("id")]
		public string? Id { get; set; }

		[JsonPropertyName("label")]
		public string? Label { get; set; }

		[JsonPropertyName("worktreePath")]
		public string? WorktreePath { get; set; }

		[JsonPropertyName("loaded")]
		public bool? Loaded { get; set; }

		[JsonPropertyName("agentProviderId")]
		public string? AgentProviderId { get; set; }

		[JsonPropertyName("editorSession")]
		public EditorSession? EditorSession { get; set; }
	}
}
