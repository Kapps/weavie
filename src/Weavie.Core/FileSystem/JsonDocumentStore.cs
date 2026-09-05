using System.Text.Json;

namespace Weavie.Core.FileSystem;

/// <summary>
/// The base for Weavie's file-backed stores (sessions, layout, theme overrides, recents, worktree registry, …).
/// It owns the backing file, the gate every member locks, the diagnostic <see cref="Log"/>, and the recovery
/// contract they all share: a missing or unreadable file starts empty, and a malformed one is copied aside to
/// <c>&lt;file&gt;.bad</c> before the store resets rather than throwing on startup. A subclass supplies only how
/// its state reads from and renders to text, so it cannot reach the file except through that contract.
/// </summary>
public abstract class JsonDocumentStore {
	private readonly IFileSystem _fileSystem;

	/// <summary>Creates a store over <paramref name="path"/>; the subclass constructor calls <see cref="Load"/>.</summary>
	/// <param name="fileSystem">The filesystem the store reads and writes through.</param>
	/// <param name="path">The backing file.</param>
	protected JsonDocumentStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
	}

	/// <summary>Diagnostic log line — read failures, malformed-file resets, and persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The gate every member locks; it guards the subclass's loaded state.</summary>
	protected Lock Gate { get; } = new();

	/// <summary>
	/// Restores the store from the persisted <paramref name="text"/>, or resets to the empty state when it is <c>null</c> (there
	/// was nothing readable). Throwing <see cref="JsonException"/> declares the text unusable, which backs the
	/// file up to <c>.bad</c> and resets the store.
	/// </summary>
	/// <param name="text">The persisted document, or <c>null</c> for the empty state.</param>
	protected abstract void Restore(string? text);

	/// <summary>The text to persist, rendered from the current state. Called under <see cref="Gate"/>.</summary>
	protected abstract string Render();

	/// <summary>
	/// Establishes the store over a file that is missing or has just been replaced as malformed — the one point
	/// a store may seed its file. Resets to the empty state without writing unless a subclass says otherwise.
	/// </summary>
	protected virtual void Establish() => Restore(null);

	/// <summary>
	/// Answers a file this store cannot use. The shared contract reports it, copies a malformed
	/// <paramref name="text"/> aside to <c>.bad</c>, and resets — override only for a store whose data is
	/// load-bearing enough that silently starting empty would be the wrong answer.
	/// </summary>
	/// <param name="text">The file's contents when <see cref="Restore"/> rejected them, or <c>null</c> when the file could not be read at all.</param>
	/// <param name="cause">Why the file is unusable.</param>
	protected virtual void OnUnusable(string? text, Exception cause) {
		ArgumentNullException.ThrowIfNull(cause);
		if (text is null) {
			Report($"could not read {FilePath}: {cause.Message}; starting empty");
			Restore(null);
			return;
		}

		Report($"{FilePath} is malformed ({cause.Message}); backing up to {Path.GetFileName(FilePath)}.bad and resetting");
		BackupBad(text);
		Establish();
	}

	/// <summary>Answers a failed write. The shared contract reports it through <see cref="Log"/>.</summary>
	/// <param name="cause">Why the write failed.</param>
	protected virtual void OnPersistFailed(Exception cause) {
		ArgumentNullException.ThrowIfNull(cause);
		Report($"could not persist: {cause.Message}");
	}

	/// <summary>Loads the backing file under the shared recovery contract.</summary>
	protected void Load() {
		lock (Gate) {
			if (!_fileSystem.FileExists(FilePath)) {
				Establish();
				return;
			}

			string text;
			try {
				text = _fileSystem.ReadAllText(FilePath);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				OnUnusable(null, ex);
				return;
			}

			try {
				Restore(text);
			} catch (JsonException ex) {
				OnUnusable(text, ex);
			}
		}
	}

	/// <summary>Atomically writes the current state, reporting a persistence failure through <see cref="Log"/>.</summary>
	protected void PersistLocked() => PersistLocked(static () => { });

	/// <summary>Atomically writes the current state and runs <paramref name="written"/> inside the failure envelope.</summary>
	/// <param name="written">Runs after a successful write (e.g. restricting the file's permissions).</param>
	protected void PersistLocked(Action written) {
		ArgumentNullException.ThrowIfNull(written);
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, Render());
			written();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			OnPersistFailed(ex);
		}
	}

	/// <summary>Raises <see cref="Log"/> with <paramref name="message"/>, tagged with the store's file name.</summary>
	/// <param name="message">The diagnostic to report.</param>
	protected void Report(string message) => Log?.Invoke($"[{Path.GetFileNameWithoutExtension(FilePath)}] {message}");

	// Losing the corrupt copy must not block recovery, so a failed backup is reported and never thrown.
	private void BackupBad(string text) {
		try {
			_fileSystem.WriteAllText(FilePath + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Report($"could not back up malformed file: {ex.Message}");
		}
	}
}
