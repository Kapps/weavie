using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Autosave writes of the editor's working buffers to disk, so the embedded <c>claude</c> (which reads disk
/// directly) sees the user's current state. Shared by the Windows and macOS hosts. The target is constrained
/// to the session workspace; a real write failure propagates so the host can log it (an autosave must never
/// silently corrupt, but it also must never crash the message loop — hence the caller's filtered catch).
/// </summary>
public static class BufferStore {
	/// <summary>
	/// Writes <paramref name="content"/> to <paramref name="path"/> via <paramref name="fileSystem"/> when the
	/// path resolves inside <paramref name="workspaceRoot"/>. Returns <see langword="false"/> without writing
	/// for an out-of-root or malformed path. A filesystem write error propagates to the caller to handle.
	/// </summary>
	/// <param name="fileSystem">The filesystem to write through.</param>
	/// <param name="workspaceRoot">The session root; writes outside it are refused.</param>
	/// <param name="path">Absolute path of the buffer to persist.</param>
	/// <param name="content">The buffer's current content.</param>
	public static bool Save(IFileSystem fileSystem, string workspaceRoot, string path, string content) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		if (!IsWithinWorkspace(workspaceRoot, path)) {
			return false;
		}

		fileSystem.WriteAllText(path, content);
		return true;
	}

	/// <summary>True when <paramref name="path"/> resolves to a location inside <paramref name="workspaceRoot"/>.</summary>
	/// <param name="workspaceRoot">The session root directory.</param>
	/// <param name="path">The candidate absolute path.</param>
	public static bool IsWithinWorkspace(string workspaceRoot, string path) {
		if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(path)) {
			return false;
		}

		string root, full;
		try {
			root = Path.GetFullPath(workspaceRoot);
			full = Path.GetFullPath(path);
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}

		string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
		return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
			|| full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
	}
}
