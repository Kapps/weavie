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
}
