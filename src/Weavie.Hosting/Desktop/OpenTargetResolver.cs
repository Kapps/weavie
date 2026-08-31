using Weavie.Core.FileSystem;

namespace Weavie.Hosting.Desktop;

/// <summary>Where an OS-delivered path should open: the workspace to use, and the file to reveal in it.</summary>
/// <param name="Root">The workspace root that will own the open.</param>
/// <param name="File">The file to reveal, or null when the path was a directory.</param>
public readonly record struct OpenTarget(string Root, string? File);

/// <summary>
/// Decides which workspace an OS-delivered path opens in. A session is workspace-scoped, so every path needs
/// one; this is the single ladder both a running instance and a cold launch obey.
/// </summary>
public static class OpenTargetResolver {
	/// <summary>
	/// Resolves <paramref name="path"/> against the currently open workspace roots and the git worktree
	/// enclosing it (<paramref name="toplevel"/>, null when there is none). A directory opens as itself. A file
	/// prefers a workspace that already contains it, then its own repository, then any open workspace — where it
	/// opens as an outside-repo file — and finally its own directory.
	/// </summary>
	public static OpenTarget Resolve(string path, IReadOnlyList<string> openRoots, string? toplevel) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(openRoots);
		string full = Path.GetFullPath(path);
		if (Directory.Exists(full)) {
			return new OpenTarget(full, null);
		}

		string directory = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"The path has no containing directory: {path}", nameof(path));
		if (openRoots.FirstOrDefault(root => PathBoundary.Contains(root, full)) is { } containing) {
			return new OpenTarget(containing, full);
		}

		if (!string.IsNullOrEmpty(toplevel)) {
			return new OpenTarget(toplevel, full);
		}

		// No repository encloses it. An open workspace can still show it — that is what opening a file outside
		// the checkout does — and only a cold launch has to invent a workspace for it.
		return new OpenTarget(openRoots.Count > 0 ? openRoots[0] : directory, full);
	}
}
