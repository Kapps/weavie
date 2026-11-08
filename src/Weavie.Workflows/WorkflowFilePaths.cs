namespace Weavie.Workflows;

/// <summary>The shared lexical containment and normalization rule for workflow file values.</summary>
public static class WorkflowFilePaths {
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
