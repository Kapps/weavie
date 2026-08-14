using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>The normalized path boundary shared by workspace file providers and streamed media.</summary>
public sealed class WorkspaceFileScope {
	private readonly string[] _roots;

	/// <summary>Allows paths inside any of <paramref name="roots"/> using Weavie's workspace path semantics.</summary>
	public WorkspaceFileScope(IEnumerable<string> roots) {
		ArgumentNullException.ThrowIfNull(roots);
		_roots = [.. roots.Select(Path.GetFullPath)];
		if (_roots.Length == 0) {
			throw new ArgumentException("At least one allowed root is required.", nameof(roots));
		}
	}

	/// <summary>Whether <paramref name="path"/> is the same as, or beneath, one of the exact allowed roots.</summary>
	public bool Contains(string path) => _roots.Any(root => PathBoundary.Contains(root, path));

	/// <summary>Resolves filesystem links and returns the physical path only when it remains in an allowed root.</summary>
	public string ResolvePhysicalPath(string path, bool allowMissingLeaf) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		if (!Path.IsPathFullyQualified(path)) {
			throw new UnauthorizedAccessException($"Path is not fully qualified: {path}");
		}
		foreach (string root in _roots) {
			try {
				return PhysicalPathBoundary.ResolveWithin(root, path, allowMissingLeaf);
			} catch (UnauthorizedAccessException) {
				// Another configured root may own the path.
			}
		}
		throw new UnauthorizedAccessException($"Path is outside the allowed workspace roots: {path}");
	}
}
