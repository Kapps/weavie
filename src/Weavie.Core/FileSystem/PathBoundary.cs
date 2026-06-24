namespace Weavie.Core.FileSystem;

/// <summary>
/// Path containment — the single guard behind every "confine a path to a directory" check (workspace, scratch,
/// worktrees, theme dirs, the file browser). Both paths are canonicalized with <see cref="Path.GetFullPath(string)"/>
/// (so <c>..</c> is resolved), then compared as "equal to the root, or under root + a directory separator". The
/// trailing separator is what stops a sibling like <c>/repo-evil</c> from passing as inside <c>/repo</c>.
/// Confinement is by normalized path string, not by resolved symlink target.
/// </summary>
public static class PathBoundary {
	/// <summary>
	/// True when <paramref name="path"/> is the same as, or inside, <paramref name="root"/>, compared
	/// case-insensitively (the editor/workspace path semantics; matches Windows and Weavie's worktree naming).
	/// </summary>
	public static bool Contains(string root, string path) => Contains(root, path, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True when <paramref name="path"/> is the same as, or inside, <paramref name="root"/>, using
	/// <paramref name="comparison"/> for the path comparison (e.g. an OS-dependent one for a case-sensitive
	/// filesystem). False if either path is empty or cannot be normalized.
	/// </summary>
	public static bool Contains(string root, string path, StringComparison comparison) {
		if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) {
			return false;
		}

		string fullRoot, fullPath;
		try {
			fullRoot = Path.GetFullPath(root);
			fullPath = Path.GetFullPath(path);
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}

		if (string.Equals(fullPath, fullRoot, comparison)) {
			return true;
		}

		string rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
			? fullRoot
			: fullRoot + Path.DirectorySeparatorChar;
		return fullPath.StartsWith(rootWithSeparator, comparison);
	}
}
