using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Autosave writes of the editor's working buffers to disk, so the embedded <c>claude</c> (which reads disk
/// directly) sees the user's current state. The target is constrained to the session workspace; a write
/// failure propagates so the host can log it (the caller's filtered catch keeps it from crashing the message loop).
/// </summary>
public static class BufferStore {
	/// <summary>
	/// Writes <paramref name="content"/> to <paramref name="path"/> when it resolves inside
	/// <paramref name="workspaceRoot"/>. Returns <see langword="false"/> without writing for an out-of-root or
	/// malformed path. A filesystem write error propagates to the caller.
	/// </summary>
	public static bool Save(IFileSystem fileSystem, string workspaceRoot, string path, string content) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		if (!IsWithinWorkspace(workspaceRoot, path)) {
			return false;
		}

		fileSystem.WriteAllText(path, content);
		return true;
	}

	/// <summary>True when <paramref name="path"/> resolves to a location inside <paramref name="workspaceRoot"/>.</summary>
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
