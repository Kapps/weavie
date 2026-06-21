using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>One entry the file browser shows: leaf <paramref name="Name"/>, absolute <paramref name="Path"/>, and whether it's a directory.</summary>
public readonly record struct BrowserEntry(string Name, string Path, bool IsDirectory);

/// <summary>
/// Lists directories for the contextual file browser, scoped to one workspace/session root. Resolves a
/// requested path, clamps it inside the root so the browser can't walk out of the workspace (e.g. via
/// <c>..</c>), and returns entries sorted directories-first then by name. Pure logic over
/// <see cref="IFileSystem"/> so every host shares it.
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
	/// inside it). Directories first, then files, each ordered case-insensitively. Empty if the resolved
	/// directory doesn't exist.
	/// </summary>
	public IReadOnlyList<BrowserEntry> List(string? requestedPath) {
		string target = string.IsNullOrEmpty(requestedPath)
			? Root
			: Path.GetFullPath(Path.Combine(Root, requestedPath));
		if (!IsWithinRoot(target)) {
			target = Root; // deny escape attempts (e.g. ../../) by falling back to the root
		}

		if (!_fileSystem.DirectoryExists(target)) {
			return [];
		}

		return [.. _fileSystem.EnumerateDirectory(target)
			.OrderByDescending(entry => entry.IsDirectory)
			.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
			.Select(entry => new BrowserEntry(entry.Name, Path.Combine(target, entry.Name), entry.IsDirectory))];
	}

	private bool IsWithinRoot(string path) {
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (string.Equals(path, Root, comparison)) {
			return true;
		}

		string rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
		return path.StartsWith(rootWithSeparator, comparison);
	}
}
