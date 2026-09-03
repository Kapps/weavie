using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Serves the editor's host-backed file provider against one session filesystem. Any path the operating system
/// lets the host read is served: the worktree, the scratch directory (see <see cref="ScratchStore"/>), and files
/// the user opens from anywhere else.
/// </summary>
public sealed class FileProviderService {
	private readonly IFileSystem _fileSystem;

	/// <summary>Reads and writes through <paramref name="fileSystem"/>, the session filesystem.</summary>
	/// <param name="fileSystem">The session filesystem the editor reads/writes through.</param>
	public FileProviderService(IFileSystem fileSystem) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	/// <summary>Returns the file's metadata, or <c>exists:false</c> for a missing path.</summary>
	public FileStat Stat(string path) {
		_fileSystem.TryGetStat(path, out var stat);
		return stat;
	}

	/// <summary>Returns the file's content and stat, a clean FileNotFound, or a read error.</summary>
	public FileReadResult Read(string path) {
		if (!_fileSystem.FileExists(path)) {
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
	/// Whether an open of <paramref name="path"/> may proceed. The existence gate <c>FileOpener</c> checks before
	/// pushing an <c>open-file</c> — the content itself is read later by the working copy (or the media pane)
	/// through the fs-read messages above.
	/// </summary>
	public bool CanRead(string path) => _fileSystem.FileExists(path);

	/// <summary>Reads a file's text, or <c>null</c> for a missing or unreadable path.</summary>
	public string? ReadText(string path) {
		if (!_fileSystem.FileExists(path)) {
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
		try {
			_fileSystem.WriteAllText(path, content);
			_fileSystem.TryGetStat(path, out var stat);
			return FileWriteResult.Success(stat);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return FileWriteResult.Failure(ex.Message);
		}
	}
}

/// <summary>The result of reading a session file.</summary>
public sealed record FileReadResult(bool Ok, string? Content, FileStat Stat, string? Code, string? Error) {
	/// <summary>A missing file.</summary>
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
