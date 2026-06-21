using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Loads, persists, and serves the per-workspace editor session at
/// <c>~/.weavie/workspaces/&lt;id&gt;/editor-session.json</c>. The host owns one per workspace window; the
/// web is the only writer (opening files / moving the cursor → debounced <c>editor-session-changed</c> →
/// <see cref="Update"/>), and on launch the host pushes <see cref="BuildRestoreJson()"/> so the editor
/// reopens its files at their saved positions. Writes are atomic; a malformed file is backed up to
/// <c>editor-session.json.bad</c> and reset. See <c>docs/specs/editor-session.md</c>.
/// </summary>
public sealed class EditorSessionStore {
	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private EditorSession _current;

	/// <summary>Creates a store over <paramref name="filePath"/>, loading the persisted session now.</summary>
	public EditorSessionStore(IFileSystem fileSystem, string filePath) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(filePath);
		_fileSystem = fileSystem;
		FilePath = filePath;
		lock (_gate) {
			_current = LoadLocked();
		}
	}

	/// <summary>
	/// Raised (off the UI thread) when the session changes via <see cref="Update"/>. The web is the sole
	/// writer, so hosts don't re-push on this (that would echo); it exists for a future MCP "open file"
	/// capability that would change the session host-side.
	/// </summary>
	public event Action<EditorSession>? Changed;

	/// <summary>Diagnostic log line — load failures, malformed-file resets, and skipped (missing) files.</summary>
	public event Action<string>? Log;

	/// <summary>The editor-session file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The current session. Never null.</summary>
	public EditorSession Current {
		get { lock (_gate) { return _current; } }
	}

	/// <summary>Replaces the session (from a web <c>editor-session-changed</c>) and persists it atomically.</summary>
	public void Update(EditorSession session) {
		ArgumentNullException.ThrowIfNull(session);
		lock (_gate) {
			_current = session;
			PersistLocked(_current);
		}

		Changed?.Invoke(session);
	}

	/// <summary>
	/// Builds the host→web <c>set-editor-session</c> message for the current session (open file paths + opaque
	/// view state). No file content rides along — the web reopens each file as a working copy from disk. Files
	/// that no longer exist are skipped and logged; if the active file was skipped, <c>active</c> is nulled.
	/// </summary>
	public string BuildRestoreJson() {
		EditorSession session;
		lock (_gate) {
			session = _current;
		}

		return BuildRestoreJson(session, _fileSystem, workspaceRoot: null, sessionId: null, line => Log?.Invoke(line));
	}

	/// <summary>
	/// Builds the host→web <c>set-editor-session</c> message for an arbitrary <paramref name="session"/>: open
	/// file paths + opaque view state, no file content. Files that no longer exist are skipped and logged via
	/// <paramref name="log"/>; if the active file was skipped, <c>active</c> is nulled. Static so a per-session
	/// switch can build the message for an in-memory <see cref="EditorSession"/> without its own store.
	/// <para>
	/// <paramref name="workspaceRoot"/> (when non-null) scopes the restore to this session's tree: an open
	/// entry that is neither a scratch buffer nor inside the root is dropped, because it belongs to a different
	/// session's worktree — restoring it here would fail as out-of-root and surface a blank editor. This also
	/// self-heals an <c>editor-session.json</c> polluted with a foreign worktree path.
	/// </para>
	/// <para>
	/// <paramref name="sessionId"/> stamps the message with the owning session so the page can echo it on the
	/// next <c>editor-session-changed</c> and the host can reject a stale cross-session write (see
	/// <c>HandleEditorSessionChanged</c>).
	/// </para>
	/// </summary>
	public static string BuildRestoreJson(EditorSession session, IFileSystem fileSystem, string? workspaceRoot, string? sessionId, Action<string>? log) {
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(fileSystem);

		var open = new List<object>();
		var surviving = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in session.Open) {
			if (!fileSystem.FileExists(entry.Path)) {
				log?.Invoke($"[editor-session] open file no longer exists; skipping {entry.Path}");
				continue;
			}

			// A scratch buffer lives outside the workspace root (its temp dir); every other tab must be inside
			// it. A tab outside the root belongs to another session's worktree and would be refused as
			// out-of-root, showing a blank editor.
			if (workspaceRoot is not null && !entry.Scratch && !BufferStore.IsWithinWorkspace(workspaceRoot, entry.Path)) {
				log?.Invoke($"[editor-session] open file is outside this session's workspace; skipping {entry.Path}");
				continue;
			}

			surviving.Add(entry.Path);
			open.Add(new {
				path = entry.Path,
				viewState = entry.ViewState,
				preview = entry.Preview,
				pinned = entry.Pinned,
				scratch = entry.Scratch,
			});
		}

		string? active = session.Active is { } a && surviving.Contains(a) ? a : null;
		var message = new {
			type = "set-editor-session",
			sessionId,
			session = new { active, open },
		};
		return JsonSerializer.Serialize(message, EditorSessionSerialization.MessageOptions);
	}

	private EditorSession LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return EditorSession.Empty;
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[editor-session] could not read {FilePath}: {ex.Message}; using empty session");
			return EditorSession.Empty;
		}

		if (!EditorSessionSerialization.TryDeserialize(text, out var parsed, out string? error) || parsed is null) {
			Log?.Invoke($"[editor-session] {FilePath} is malformed ({error}); backing up to editor-session.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "editor-session", Log);
			PersistLocked(EditorSession.Empty);
			return EditorSession.Empty;
		}

		return parsed;
	}

	private void PersistLocked(EditorSession session) {
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, EditorSessionSerialization.Serialize(session));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[editor-session] could not persist session: {ex.Message}");
		}
	}
}
