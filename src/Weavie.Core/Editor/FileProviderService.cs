using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Serves the editor's host-backed <c>file://</c> provider: answers correlated
/// <c>fs-stat</c>/<c>fs-read</c>/<c>fs-write</c> requests against the session filesystem, scoped to the
/// workspace. Each method returns ready-to-send reply JSON (built by <see cref="FileProviderProtocol"/>).
/// Access outside the workspace is refused: a read becomes a clean FileNotFound (the provider falls through),
/// a write becomes an error the page surfaces.
/// </summary>
public sealed class FileProviderService {
	private readonly IFileSystem _fileSystem;
	private readonly string _workspaceRoot;
	private readonly string _scratchRoot;

	/// <summary>Constrains all access to <paramref name="workspaceRoot"/> and <paramref name="scratchRoot"/>.</summary>
	/// <param name="fileSystem">The session filesystem the editor reads/writes through.</param>
	/// <param name="workspaceRoot">The session root; access outside it (and the scratch root) is refused.</param>
	/// <param name="scratchRoot">
	/// The workspace's scratch directory (see <see cref="ScratchStore"/>) — a second allowed root, so untitled
	/// buffers living outside the workspace are still readable/writable.
	/// </param>
	public FileProviderService(IFileSystem fileSystem, string workspaceRoot, string scratchRoot) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentException.ThrowIfNullOrEmpty(scratchRoot);
		_fileSystem = fileSystem;
		_workspaceRoot = workspaceRoot;
		_scratchRoot = scratchRoot;
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
	public string Read(string id, string path) {
		if (!IsAllowed(path) || !_fileSystem.FileExists(path)) {
			return FileProviderProtocol.ReadNotFound(id);
		}

		try {
			string content = _fileSystem.ReadAllText(path);
			_fileSystem.TryGetStat(path, out var stat);
			return FileProviderProtocol.ReadResult(id, content, stat);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return FileProviderProtocol.ReadError(id, ex.Message);
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

	private bool IsAllowed(string path) =>
		BufferStore.IsWithinWorkspace(_workspaceRoot, path)
		|| BufferStore.IsWithinWorkspace(_scratchRoot, path);
}
