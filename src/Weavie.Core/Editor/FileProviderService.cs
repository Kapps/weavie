using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Serves the editor's host-backed file provider against one session filesystem, scoped to its workspace and
/// scratch directory.
/// Out-of-workspace access is refused: a read becomes a clean FileNotFound, a write an error the page surfaces.
/// </summary>
public sealed class FileProviderService {
	private readonly IFileSystem _fileSystem;
	private readonly WorkspaceFileScope _scope;

	/// <summary>Constrains all access to <paramref name="workspaceRoot"/> and <paramref name="scratchRoot"/>.</summary>
	/// <param name="fileSystem">The session filesystem the editor reads/writes through.</param>
	/// <param name="workspaceRoot">The session root; access outside it (and the scratch root) is refused.</param>
	/// <param name="scratchRoot">The scratch directory (see <see cref="ScratchStore"/>) — a second allowed root for untitled buffers.</param>
	public FileProviderService(IFileSystem fileSystem, string workspaceRoot, string scratchRoot) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentException.ThrowIfNullOrEmpty(scratchRoot);
		_fileSystem = fileSystem;
		_scope = new WorkspaceFileScope([workspaceRoot, scratchRoot]);
	}

	/// <summary>Returns the file's metadata, or <c>exists:false</c> for a missing/out-of-workspace path.</summary>
	public FileStat Stat(string path) {
		if (!IsAllowed(path)) {
			return default;
		}

		_fileSystem.TryGetStat(path, out var stat);
		return stat;
	}

	/// <summary>Returns the file's content and stat, a clean FileNotFound, or a read error.</summary>
	public FileReadResult Read(string path) {
		if (!IsAllowed(path) || !_fileSystem.FileExists(path)) {
			return FileReadResult.NotFound;
		}

		try {
			if (!_fileSystem.TryReadAllText(path, out string content)) {
				return FileReadResult.Failure("Binary files cannot be opened as text.");
			}

			_fileSystem.TryGetStat(path, out var stat);
			return FileReadResult.Success(content, stat);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return FileReadResult.Failure(ex.Message);
		}
	}

	/// <summary>
	/// Whether an open of <paramref name="path"/> may proceed: inside an allowed root and present on disk.
	/// The confinement gate <c>FileOpener</c> checks before pushing an <c>open-file</c> — the content itself
	/// is read later by the working copy (or the media pane) through the fs-read messages above.
	/// </summary>
	public bool CanRead(string path) => IsAllowed(path) && _fileSystem.FileExists(path);

	/// <summary>
	/// Reads a file's text when it's inside an allowed root (the workspace or scratch), else <c>null</c> for an
	/// out-of-workspace, missing, or unreadable path. The single validated read every host-side file *open*
	/// shares — the editor provider above, plus <c>FileOpener</c> (reveal-file / MCP <c>openFile</c>) and the
	/// openDiff baseline — so the same confinement is enforced in one place and can't be bypassed by a caller.
	/// Confinement is by normalized path (<c>Path.GetFullPath</c>), not by resolved link target: an in-tree
	/// symlink that points outside is followed, which is acceptable under the trusted-opened-repo model.
	/// </summary>
	public string? ReadIfAllowed(string path) {
		if (!IsAllowed(path) || !_fileSystem.FileExists(path)) {
			return null;
		}

		try {
			return _fileSystem.TryReadAllText(path, out string content) ? content : null;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	/// <summary>Persists the buffer to disk and returns the post-write stat, or an error.</summary>
	public FileWriteResult Write(string path, string content) {
		ArgumentNullException.ThrowIfNull(content);
		if (!IsAllowed(path)) {
			return FileWriteResult.Failure("Path is outside the workspace.");
		}

		try {
			_fileSystem.WriteAllText(path, content);
			_fileSystem.TryGetStat(path, out var stat);
			return FileWriteResult.Success(stat);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return FileWriteResult.Failure(ex.Message);
		}
	}

	private bool IsAllowed(string path) => _scope.Contains(path);
}

/// <summary>The result of reading a session file.</summary>
public sealed record FileReadResult(bool Ok, string? Content, FileStat Stat, string? Code, string? Error) {
	/// <summary>A missing or out-of-scope file.</summary>
	public static FileReadResult NotFound { get; } = new(false, null, default, "FileNotFound", null);

	/// <summary>Builds a successful text read.</summary>
	public static FileReadResult Success(string content, FileStat stat) => new(true, content, stat, null, null);

	/// <summary>Builds a failed read.</summary>
	public static FileReadResult Failure(string error) => new(false, null, default, null, error);
}

/// <summary>The result of writing a session file.</summary>
public sealed record FileWriteResult(bool Ok, FileStat Stat, string? Error) {
	/// <summary>Builds a successful write.</summary>
	public static FileWriteResult Success(FileStat stat) => new(true, stat, null);

	/// <summary>Builds a failed write.</summary>
	public static FileWriteResult Failure(string error) => new(false, default, error);
}
