using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>The disposition of one host-backed <c>fs-read</c>.</summary>
public enum FileProviderReadStatus {
	/// <summary>The text file was read successfully.</summary>
	Success,
	/// <summary>The requested path is unavailable to the provider (missing or outside its scope).</summary>
	NotFound,
	/// <summary>The path exists but could not be read as text.</summary>
	Failed,
}

/// <summary>The host response to one <c>fs-read</c>, retaining text only when the read reached disk successfully.</summary>
public sealed class FileProviderReadOutcome {
	internal FileProviderReadOutcome(string response, string? content, FileProviderReadStatus status) {
		Response = response;
		Content = content;
		Status = status;
	}

	/// <summary>The serialized <c>fs-read-result</c> sent to the page.</summary>
	public string Response { get; }

	/// <summary>The successfully read text, or <see langword="null"/> when no text reached the page.</summary>
	public string? Content { get; }

	/// <summary>Whether the file was read, missing, or failed without discarding an existing tracker entry.</summary>
	public FileProviderReadStatus Status { get; }
}

/// <summary>
/// Serves the editor's host-backed <c>file://</c> provider: answers <c>fs-stat</c>/<c>fs-read</c>/<c>fs-write</c>
/// against the session filesystem, scoped to the workspace, returning reply JSON from <see cref="FileProviderProtocol"/>.
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

	/// <summary>Answers <c>fs-stat</c>: the file's metadata, or <c>exists:false</c> for a missing/out-of-workspace path.</summary>
	public string Stat(string id, string path) {
		if (!IsAllowed(path)) {
			return FileProviderProtocol.StatResult(id, default);
		}

		_fileSystem.TryGetStat(path, out var stat);
		return FileProviderProtocol.StatResult(id, stat);
	}

	/// <summary>Answers <c>fs-read</c>: the file's content + etag, a clean FileNotFound, or a loud read error.</summary>
	public string Read(string id, string path) => ReadWithOutcome(id, path).Response;

	/// <summary>
	/// Answers <c>fs-read</c> while retaining successful text for a same-operation observer (for example authored-line
	/// tracking) without requiring a second filesystem read.
	/// </summary>
	public FileProviderReadOutcome ReadWithOutcome(string id, string path) {
		if (!IsAllowed(path) || !_fileSystem.FileExists(path)) {
			return new FileProviderReadOutcome(FileProviderProtocol.ReadNotFound(id), null, FileProviderReadStatus.NotFound);
		}

		try {
			if (!_fileSystem.TryReadAllText(path, out string content)) {
				return new FileProviderReadOutcome(
					FileProviderProtocol.ReadError(id, "Binary files cannot be opened as text."), null, FileProviderReadStatus.Failed);
			}

			_fileSystem.TryGetStat(path, out var stat);
			return new FileProviderReadOutcome(FileProviderProtocol.ReadResult(id, content, stat), content, FileProviderReadStatus.Success);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return new FileProviderReadOutcome(FileProviderProtocol.ReadError(id, ex.Message), null, FileProviderReadStatus.Failed);
		}
	}

	/// <summary>Whether <paramref name="path"/> is inside this provider's workspace or scratch scope.</summary>
	public bool Allows(string path) => IsAllowed(path);

	/// <summary>
	/// Whether an open of <paramref name="path"/> may proceed: inside an allowed root and present on disk.
	/// The confinement gate <c>FileOpener</c> checks before pushing an <c>open-file</c> — the content itself
	/// is read later by the working copy (or the media pane) through the fs-read messages above.
	/// </summary>
	public bool CanRead(string path) => Allows(path) && _fileSystem.FileExists(path);

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

	/// <summary>Answers <c>fs-write</c>: persists the buffer to disk and returns the post-write etag, or an error.</summary>
	public string Write(string id, string path, string content) {
		ArgumentNullException.ThrowIfNull(content);
		if (!IsAllowed(path)) {
			return FileProviderProtocol.WriteError(id, "Path is outside the workspace.");
		}

		try {
			_fileSystem.WriteAllText(path, content);
			_fileSystem.TryGetStat(path, out var stat);
			return FileProviderProtocol.WriteResult(id, stat);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return FileProviderProtocol.WriteError(id, ex.Message);
		}
	}

	private bool IsAllowed(string path) => _scope.Contains(path);
}
