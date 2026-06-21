namespace Weavie.Core.Workspaces;

/// <summary>
/// Shared workspace-naming helpers, so every host derives the same window-title / shell label from a path.
/// </summary>
public static class WorkspaceNaming {
	/// <summary>
	/// The folder's leaf name for the window title / shell label (e.g. <c>weavie</c> for <c>/src/weavie</c>);
	/// falls back to the full root when there's no leaf (a drive root).
	/// </summary>
	public static string Label(string root) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } leaf
			? leaf
			: root;
	}
}
