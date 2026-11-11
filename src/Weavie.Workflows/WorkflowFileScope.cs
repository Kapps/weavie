namespace Weavie.Workflows;

/// <summary>Where a run's file values may live: the session worktree and, once a run grants it, its documents directory.</summary>
public sealed record WorkflowFileScope(string Worktree, string? Documents) {
	/// <summary>
	/// Resolves a worktree-relative file, or an absolute file inside the documents directory, without accessing the
	/// filesystem; <paramref name="label"/> is the path as evidence names it.
	/// </summary>
	public bool TryResolve(string path, out string label, out string full) {
		if (!Path.IsPathRooted(path)) return TryContain(Worktree, path, out label, out full);
		label = full = "";
		try {
			if (Documents is null || !TryContain(Documents, Path.GetRelativePath(Documents, path), out _, out full)) return false;
			label = full;
			return true;
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}
	}

	private static bool TryContain(string directory, string path, out string relative, out string full) {
		relative = full = "";
		try {
			if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
			string root = Path.GetFullPath(directory);
			string resolved = Path.GetFullPath(path, root);
			string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
			if (resolved == root || !resolved.StartsWith(prefix, StringComparison.Ordinal)) return false;
			relative = Path.GetRelativePath(root, resolved);
			full = resolved;
			return true;
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}
	}
}
