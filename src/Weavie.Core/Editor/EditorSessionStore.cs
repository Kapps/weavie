using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Loads, persists, and serves the per-workspace editor session at
/// <c>~/.weavie/workspaces/&lt;id&gt;/editor-session.json</c>. The host owns one per workspace window; the
/// web is the only writer (the user opens files / moves the cursor → debounced <c>editor-session-changed</c>
/// → <see cref="Update"/>), and on launch the host reads disk and pushes <see cref="BuildRestoreJson(string)"/> so
/// the editor reopens its files at their saved positions. Writes are atomic; a malformed file is backed up
/// to <c>editor-session.json.bad</c> and reset. Modeled on <c>LayoutStore</c>; sibling of
/// <see cref="EditorStore"/>. See <c>docs/specs/editor-session.md</c>.
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
	/// writer today, so hosts don't re-push on this (that would echo); it exists for parity with
	/// <c>LayoutStore</c> and a future MCP "open file" capability that would change the session host-side.
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
	/// Builds the host→web <c>set-editor-session</c> message: the current session (open file paths + opaque
	/// view state). No file content rides along — the web reopens each file as a working copy resolved from
	/// disk through the host file provider. Files that no longer exist are skipped and logged; if the active
	/// file was skipped, <c>active</c> is nulled. <paramref name="owner"/> is the id of the session these tabs
	/// belong to; the web echoes it on later editor messages so a post-switch send is attributed correctly.
	/// </summary>
	public string BuildRestoreJson(string owner) {
		EditorSession session;
		lock (_gate) {
			session = _current;
		}

		return BuildRestoreJson(session, _fileSystem, line => Log?.Invoke(line), owner);
	}

	/// <summary>
	/// Builds the host→web <c>set-editor-session</c> message for an arbitrary <paramref name="session"/>:
	/// open file paths + opaque view state, no file content (the web reopens each as a working copy read
	/// from disk through the host file provider). Files that no longer exist (checked against
	/// <paramref name="fileSystem"/>) are skipped and logged via <paramref name="log"/>; if the active file
	/// was skipped, <c>active</c> is nulled. <paramref name="owner"/> is the id of the session these tabs
	/// belong to (stamped so the page can attribute its echoed editor messages back to it). Static so a
	/// per-session switch can build the message for a session's in-memory <see cref="EditorSession"/> without
	/// its own store.
	/// </summary>
	public static string BuildRestoreJson(EditorSession session, IFileSystem fileSystem, Action<string>? log, string owner) {
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(fileSystem);

		var open = new List<object>();
		var surviving = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in session.Open) {
			if (!fileSystem.FileExists(entry.Path)) {
				log?.Invoke($"[editor-session] open file no longer exists; skipping {entry.Path}");
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
			owner,
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
			BackupBadFileLocked(text);
			PersistLocked(EditorSession.Empty);
			return EditorSession.Empty;
		}

		return parsed;
	}

	private void BackupBadFileLocked(string text) {
		try {
			_fileSystem.WriteAllText(FilePath + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[editor-session] could not back up malformed session: {ex.Message}");
		}
	}

	private void PersistLocked(EditorSession session) {
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, EditorSessionSerialization.Serialize(session));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[editor-session] could not persist session: {ex.Message}");
		}
	}
}
