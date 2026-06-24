using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Autosaves editor working buffers to disk so the embedded <c>claude</c> sees current state. Constrained to
/// the session workspace; a write failure propagates so the host can log it.
/// </summary>
public static class BufferStore {
	/// <summary>
	/// Writes <paramref name="content"/> to <paramref name="path"/> when it resolves inside
	/// <paramref name="workspaceRoot"/>; returns <see langword="false"/> without writing otherwise. A write error propagates.
	/// </summary>
	public static bool Save(IFileSystem fileSystem, string workspaceRoot, string path, string content) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		if (!IsWithinWorkspace(workspaceRoot, path)) {
			return false;
		}

		fileSystem.WriteAllText(path, content);
		return true;
	}

	/// <summary>True when <paramref name="path"/> resolves to a location inside <paramref name="workspaceRoot"/>. The workspace-scoped name for <see cref="PathBoundary.Contains(string, string)"/>.</summary>
	public static bool IsWithinWorkspace(string workspaceRoot, string path) => PathBoundary.Contains(workspaceRoot, path);
}
