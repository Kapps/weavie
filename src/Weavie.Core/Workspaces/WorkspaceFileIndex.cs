using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>The files and directories observed by one non-Git navigation walk.</summary>
/// <param name="Files">Absolute paths for every discovered file.</param>
/// <param name="Directories">Absolute paths for every visited directory.</param>
public sealed record WorkspaceFileIndexSnapshot(
	IReadOnlyList<string> Files,
	IReadOnlyList<string> Directories);

/// <summary>
/// Builds a flat list of every file under one workspace root (pruning <see cref="WorkspacePaths"/> noise dirs)
/// for the omnibar's "Go to File" quick-open.
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
	public IReadOnlyList<string> List() => ListSnapshot().Files;

	/// <summary>Returns every file and visited directory from one navigation walk.</summary>
	public WorkspaceFileIndexSnapshot ListSnapshot() {
		if (!_fileSystem.DirectoryExists(Root)) {
			return new WorkspaceFileIndexSnapshot([], []);
		}

		var files = new List<string>();
		var directories = new List<string>();
		Walk(Root, files, directories);
		files.Sort(StringComparer.OrdinalIgnoreCase);
		directories.Sort(StringComparer.OrdinalIgnoreCase);
		return new WorkspaceFileIndexSnapshot(files, directories);
	}

	/// <summary>Depth-first walk collecting navigation paths while pruning ignored directories.</summary>
	private void Walk(string directory, List<string> files, List<string> directories) {
		// Iterative DFS so a deep tree can't blow the stack.
		var stack = new Stack<string>();
		stack.Push(directory);

		while (stack.Count > 0) {
			string current = stack.Pop();
			directories.Add(current);
			foreach (var entry in _fileSystem.EnumerateDirectory(current)) {
				string fullPath = Path.Combine(current, entry.Name);
				if (entry.IsDirectory) {
					if (!WorkspacePaths.IsIgnoredSegment(entry.Name)) {
						stack.Push(fullPath);
					}
				} else {
					files.Add(fullPath);
				}
			}
		}
	}
}
