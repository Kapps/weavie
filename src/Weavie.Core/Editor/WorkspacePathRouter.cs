namespace Weavie.Core.Editor;

/// <summary>
/// Routes an absolute <c>fs-stat</c>/<c>fs-read</c>/<c>fs-write</c> path to the session whose worktree owns it.
/// Route by path, NOT by the active session: a switch landing mid-request would otherwise refuse the outgoing
/// session's read (spurious "Unable to read file") and drop its post-swap working-copy flush (lost edits).
/// </summary>
public static class WorkspacePathRouter {
	/// <summary>
	/// The index into <paramref name="roots"/> whose root longest-prefix-contains <paramref name="path"/>, or
	/// <c>-1</c> when none does. Longest-prefix wins so a nested root takes precedence, resolving ties deterministically.
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
