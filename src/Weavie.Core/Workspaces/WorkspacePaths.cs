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

	/// <summary>
	/// Lowercases a leading Windows drive letter (<c>C:\…</c> → <c>c:\…</c>), leaving every other path
	/// untouched. The editor's host-backed <c>file://</c> provider matches URIs case-SENSITIVELY, and the web
	/// canonicalizes native paths this same way before building a <c>file://</c> URI (mirrors
	/// <c>editor/fs-path.ts</c> <c>canonicalFsPath</c>) — Monaco's <c>model.uri.fsPath</c> also lowercases the
	/// drive. So every native path the host hands the editor (e.g. an <c>open-file</c> message) must carry the
	/// SAME spelling, or the same on-disk file reaches the editor as two distinct URIs: a second working copy
	/// opens and the active-file/tab tracking can't tell it's already open. Only the drive letter is folded
	/// (the on-disk filename case and separators survive), so the result stays a real, openable path.
	/// </summary>
	public static string CanonicalFsPath(string path) {
		ArgumentNullException.ThrowIfNull(path);
		if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) {
			return char.ToLowerInvariant(path[0]) + path[1..];
		}

		return path;
	}
}
