using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>One entry the file browser shows: leaf <paramref name="Name"/>, absolute <paramref name="Path"/>, and whether it's a directory.</summary>
public readonly record struct BrowserEntry(string Name, string Path, bool IsDirectory);

/// <summary>
/// Lists directories for the file browser and the omnibar's open-by-path completion. <see cref="Root"/> is the
/// base a relative request resolves against, not a fence — an absolute request lists that directory, matching
/// the file provider, which serves any readable path. Entries are sorted directories-first then by name.
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

	/// <summary>The absolute workspace root a relative request resolves against.</summary>
	public string Root { get; }

	/// <summary>
	/// Lists the immediate entries of <paramref name="requestedPath"/> — absolute as itself, relative against
	/// <see cref="Root"/>, empty as the root — directories first then files, each case-insensitive. A malformed
	/// or missing directory throws, so the caller replies an error rather than an empty listing that reads as an
	/// empty directory.
	/// </summary>
	public IReadOnlyList<BrowserEntry> List(string? requestedPath) {
		string target = string.IsNullOrEmpty(requestedPath)
			? Root
			: Path.GetFullPath(Path.Combine(Root, requestedPath));
		if (!_fileSystem.DirectoryExists(target)) {
			throw new DirectoryNotFoundException($"Directory not found: {target}");
		}

		return [.. _fileSystem.EnumerateDirectory(target)
			.OrderByDescending(entry => entry.IsDirectory)
			.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
			.Select(entry => new BrowserEntry(entry.Name, Path.Combine(target, entry.Name), entry.IsDirectory))];
	}
}
