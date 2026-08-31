namespace Weavie.Hosting;

// The web addresses files by absolute path, but several features are worktree-relative by nature: git wants a
// path relative to the worktree it runs in, and test rules are globs over checkout-relative paths. Both the
// resolution and the message a caller shows when it fails live here, so every surface refuses the same way.
public sealed partial class HostCore {
	// A path outside this session's worktree resolves to null rather than reaching git as an unanchored argument.
	private static string? WorktreeRelativePath(HostSession session, string absolutePath) {
		if (string.IsNullOrWhiteSpace(absolutePath)) {
			return null;
		}

		string relative = Path.GetRelativePath(session.WorkspaceRoot, Path.GetFullPath(absolutePath))
			.Replace('\\', '/');
		return relative.Length == 0 || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
			? null
			: relative;
	}

	private static string NotInWorktree(HostSession session, string path) =>
		$"'{path}' isn't inside {session.WorkspaceRoot}.";
}
