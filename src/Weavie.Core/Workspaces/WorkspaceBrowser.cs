using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>One entry the file browser shows: leaf <paramref name="Name"/>, absolute <paramref name="Path"/>, and whether it's a directory.</summary>
public readonly record struct BrowserEntry(string Name, string Path, bool IsDirectory);

/// <summary>
/// Lists directories for the contextual file browser, clamped inside one workspace root so the browser
/// can't walk out (e.g. via <c>..</c>). Entries are sorted directories-first then by name.
/// </summary>
public sealed class WorkspaceBrowser {
	private readonly IFileSystem _fileSystem;

	/// <summary>Creates a browser rooted at <paramref name="root"/> over <paramref name="fileSystem"/>.</summary>
	public WorkspaceBrowser(IFileSystem fileSystem, string root) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(root);
		_fileSystem = fileSystem;
		Root = Path.GetFullPath(root);
	}

	/// <summary>The absolute workspace root the browser is scoped to.</summary>
	public string Root { get; }

	/// <summary>
	/// Lists the immediate entries of <paramref name="requestedPath"/> (defaulting to the root, and clamped
	/// inside it), directories first then files, each case-insensitive. Empty if the directory doesn't exist.
	/// </summary>
	public IReadOnlyList<BrowserEntry> List(string? requestedPath) {
		if (TryResolve(requestedPath) is not { } target || !_fileSystem.DirectoryExists(target)) {
			return [];
		}

		return [.. _fileSystem.EnumerateDirectory(target)
			.OrderByDescending(entry => entry.IsDirectory)
			.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
			.Select(entry => new BrowserEntry(entry.Name, Path.Combine(target, entry.Name), entry.IsDirectory))];
	}

	/// <summary>
	/// The absolute directory <see cref="List"/> lists for <paramref name="requestedPath"/> — the root when empty
	/// or escaping (e.g. via <c>..</c>) — or <see langword="null"/> for a malformed path that has no listing.
	/// </summary>
	public string? TryResolve(string? requestedPath) {
		string target;
		try {
			target = string.IsNullOrEmpty(requestedPath)
				? Root
				: Path.GetFullPath(Path.Combine(Root, requestedPath));
		} catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) {
			return null; // a malformed path has no listing — never throw past the caller's reply
		}

		return IsWithinRoot(target) ? target : Root;
	}

	// Case-sensitive off Windows: the browser is the only confinement guard run against a case-sensitive
	// filesystem's real paths (the editor's IsWithinWorkspace deliberately folds case everywhere).
	private bool IsWithinRoot(string path) =>
		PathBoundary.Contains(Root, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
