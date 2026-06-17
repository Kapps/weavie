namespace Weavie.Core.Workspaces;

/// <summary>
/// Shared workspace path conventions. The canonical list of "noise" directory segments Weavie skips when
/// walking a workspace tree — dependency caches and build output that would otherwise drown a recursive
/// listing or file watcher. Centralized here so the file index (<see cref="WorkspaceFileIndex"/>) and the
/// LSP watcher (<c>WorkspaceWatcher</c>) agree on exactly one list instead of each carrying its own copy.
/// </summary>
public static class WorkspacePaths {
	/// <summary>Directory names skipped anywhere in a workspace tree (case-insensitive).</summary>
	public static readonly IReadOnlyList<string> IgnoredSegments =
		["node_modules", ".git", "bin", "obj", "dist", ".vs", ".idea", "out", "target"];

	private static readonly HashSet<string> IgnoredSet = new(IgnoredSegments, StringComparer.OrdinalIgnoreCase);

	/// <summary>True if <paramref name="segment"/> is one of the ignored directory names (case-insensitive).</summary>
	public static bool IsIgnoredSegment(string segment) => IgnoredSet.Contains(segment);

	/// <summary>True if any path segment of <paramref name="fullPath"/> is an ignored directory name.</summary>
	public static bool HasIgnoredSegment(string fullPath) {
		foreach (string segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) {
			if (IgnoredSet.Contains(segment)) {
				return true;
			}
		}

		return false;
	}
}
