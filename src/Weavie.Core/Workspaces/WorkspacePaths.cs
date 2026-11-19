namespace Weavie.Core.Workspaces;

/// <summary>
/// Shared workspace path conventions, chiefly the "noise" directory names (dependency caches, build output) the
/// walk of a plain folder skips; inside a repository git's ignore rules decide instead.
/// </summary>
public static class WorkspacePaths {
	private static readonly HashSet<string> IgnoredSet = new(
		["node_modules", ".git", "bin", "obj", "dist", ".vs", ".idea", "out", "target"],
		StringComparer.OrdinalIgnoreCase);

	/// <summary>True if <paramref name="segment"/> is one of the ignored directory names (case-insensitive).</summary>
	public static bool IsIgnoredSegment(string segment) => IgnoredSet.Contains(segment);

	/// <summary>
	/// True if <paramref name="fullPath"/> is a file whose contents decide which paths the workspace contains,
	/// so editing it redefines the inventory rather than just changing one file.
	/// </summary>
	public static bool IsIgnoreRuleFile(string fullPath) =>
		Path.GetFileName(fullPath.AsSpan()).Equals(".gitignore", StringComparison.OrdinalIgnoreCase);

	/// <summary>Replaces a leading <c>~</c> in <paramref name="path"/> with the user's home directory.</summary>
	public static string ExpandHome(string path) {
		ArgumentNullException.ThrowIfNull(path);
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (path == "~") {
			return home;
		}

		return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
			? Path.Combine(home, path[2..])
			: path;
	}

	/// <summary>
	/// The absolute form of a typed <paramref name="path"/>: <c>~</c> is the user's home, and a relative path is
	/// taken from <paramref name="basePath"/>.
	/// </summary>
	public static string Resolve(string path, string basePath) {
		ArgumentException.ThrowIfNullOrEmpty(basePath);
		return Path.GetFullPath(ExpandHome(path.Trim()), basePath);
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
