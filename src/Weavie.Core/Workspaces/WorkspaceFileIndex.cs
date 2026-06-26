using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>
/// Builds a capped, flat list of every file under one workspace root (pruning <see cref="WorkspacePaths"/>
/// noise dirs) for the omnibar's "Go to File" quick-open.
/// </summary>
public sealed class WorkspaceFileIndex {
	private readonly IFileSystem _fileSystem;

	/// <summary>Creates an index rooted at <paramref name="root"/> over <paramref name="fileSystem"/>.</summary>
	public WorkspaceFileIndex(IFileSystem fileSystem, string root) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(root);
		_fileSystem = fileSystem;
		Root = Path.GetFullPath(root);
	}

	/// <summary>The absolute workspace root the index is scoped to.</summary>
	public string Root { get; }

	/// <summary>
	/// Returns every file's absolute path, sorted case-insensitively, pruning ignored directories. The walk is
	/// unbounded: an IDE must be able to open any file, so the index never drops one — the page filters locally.
	/// </summary>
	public IReadOnlyList<string> List() {
		if (!_fileSystem.DirectoryExists(Root)) {
			return [];
		}

		var files = new List<string>();
		Walk(Root, files);
		files.Sort(StringComparer.OrdinalIgnoreCase);
		return files;
	}

	/// <summary>Depth-first walk collecting every file into <paramref name="sink"/>, pruning ignored directories.</summary>
	private void Walk(string directory, List<string> sink) {
		// Iterative DFS so a deep tree can't blow the stack.
		var stack = new Stack<string>();
		stack.Push(directory);

		while (stack.Count > 0) {
			string current = stack.Pop();
			foreach (var entry in _fileSystem.EnumerateDirectory(current)) {
				string fullPath = Path.Combine(current, entry.Name);
				if (entry.IsDirectory) {
					if (!WorkspacePaths.IsIgnoredSegment(entry.Name)) {
						stack.Push(fullPath);
					}
				} else {
					sink.Add(fullPath);
				}
			}
		}
	}
}
