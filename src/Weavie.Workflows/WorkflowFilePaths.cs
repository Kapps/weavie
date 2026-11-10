namespace Weavie.Workflows;

/// <summary>The shared lexical containment and normalization rule for workflow file values.</summary>
public static class WorkflowFilePaths {
	/// <summary>Resolves worktree-relative files or absolute files contained in the granted documents directory.</summary>
	public static bool TryResolve(string worktree, string? documentsDirectory, string path, out string label, out string full) {
		if (!Path.IsPathRooted(path)) return TryResolve(worktree, path, out label, out full);
		label = full = "";
		try {
			if (documentsDirectory is null) return false;
			string relative = Path.GetRelativePath(documentsDirectory, path);
			if (!TryResolve(documentsDirectory, relative, out _, out full)) return false;
			label = full;
			return true;
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}
	}

	/// <summary>Resolves a relative file path inside the worktree without accessing the filesystem.</summary>
	public static bool TryResolve(string worktree, string path, out string relative, out string full) {
		relative = full = "";
		try {
			if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
			string root = Path.GetFullPath(worktree);
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
