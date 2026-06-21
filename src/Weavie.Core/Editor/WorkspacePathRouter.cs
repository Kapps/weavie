namespace Weavie.Core.Editor;

/// <summary>
/// Routes an absolute file path to the session that owns it. The page addresses
/// <c>fs-stat</c>/<c>fs-read</c>/<c>fs-write</c> by absolute path, and that path uniquely identifies
/// which session's worktree the file lives in — so the host must route by path, NOT by "whichever
/// session is active right now". Routing by the active session breaks the instant a switch lands
/// mid-request (a read for the outgoing session's file is refused by the incoming session's provider,
/// surfacing as a spurious "Unable to read file"), and silently loses the outgoing session's working-copy
/// flush (an <c>fs-write</c> that arrives after the swap is refused → lost edits). Pure + Core-tested;
/// the hosting layer maps the returned index back to its <c>HostSession</c>.
/// </summary>
public static class WorkspacePathRouter {
	/// <summary>
	/// The index into <paramref name="roots"/> of the session whose workspace root best (longest matching
	/// prefix) contains <paramref name="path"/>, or <c>-1</c> when no root contains it. Longest-prefix wins
	/// so a nested root (were one ever to exist) takes precedence over an ancestor; today's worktrees never
	/// nest, but ties are resolved deterministically rather than by list order.
	/// </summary>
	public static int OwningRootIndex(IReadOnlyList<string> roots, string path) {
		ArgumentNullException.ThrowIfNull(roots);
		if (string.IsNullOrEmpty(path)) {
			return -1;
		}

		int best = -1;
		int bestLength = -1;
		for (int i = 0; i < roots.Count; i++) {
			if (!BufferStore.IsWithinWorkspace(roots[i], path)) {
				continue;
			}

			int length = NormalizedLength(roots[i]);
			if (length > bestLength) {
				bestLength = length;
				best = i;
			}
		}

		return best;
	}

	private static int NormalizedLength(string root) {
		try {
			return Path.GetFullPath(root).Length;
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return root.Length;
		}
	}
}
