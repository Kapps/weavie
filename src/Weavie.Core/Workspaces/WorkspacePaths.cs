namespace Weavie.Core.Workspaces;

/// <summary>
/// Shared workspace path conventions, chiefly the canonical list of "noise" directory segments (dependency
/// caches, build output) Weavie skips when walking a tree — one list the file index and LSP watcher agree on.
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

	/// <summary>
	/// Lowercases a leading Windows drive letter (<c>C:\…</c> → <c>c:\…</c>), else untouched. Must match the
	/// web's <c>editor/fs-path.ts</c> <c>canonicalFsPath</c> and Monaco's <c>model.uri.fsPath</c>, or one
	/// on-disk file reaches the editor as two case-distinct URIs — a duplicate copy that breaks tab tracking.
	/// </summary>
	public static string CanonicalFsPath(string path) {
		ArgumentNullException.ThrowIfNull(path);
		if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) {
			return char.ToLowerInvariant(path[0]) + path[1..];
		}

		return path;
	}
}
