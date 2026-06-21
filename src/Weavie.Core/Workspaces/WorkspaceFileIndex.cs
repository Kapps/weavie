using Weavie.Core.FileSystem;

namespace Weavie.Core.Workspaces;

/// <summary>
/// Builds a flat list of every file under one workspace root, for the omnibar's "Go to File" quick-open.
/// Walks <see cref="IFileSystem"/> recursively, pruning the noise directories in <see cref="WorkspacePaths"/>
/// and capping the result so a pathological tree can't produce an unbounded payload. Pure logic over the
/// filesystem seam, so every host shares it and it's testable in-memory.
/// </summary>
public sealed class WorkspaceFileIndex {
	/// <summary>Default ceiling on returned files; a tree larger than this is truncated (and logged).</summary>
	public const int DefaultCap = 20_000;

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

	/// <summary>Diagnostic sink (e.g. when the cap truncates the walk). Optional.</summary>
	public event Action<string>? Log;

	/// <summary>
	/// Walks the tree and returns every file's absolute path, sorted case-insensitively, pruning ignored
	/// directories and stopping once <paramref name="cap"/> files are collected (logging the truncation).
	/// </summary>
	public IReadOnlyList<string> List(int cap) {
		if (cap <= 0 || !_fileSystem.DirectoryExists(Root)) {
			return [];
		}

		var files = new List<string>();
		bool truncated = Walk(Root, files, cap);
		if (truncated) {
			Log?.Invoke($"file index truncated at {cap} files under {Root}");
		}

		files.Sort(StringComparer.OrdinalIgnoreCase);
		return files;
	}

	/// <summary>
	/// Depth-first walk collecting files into <paramref name="sink"/>. Returns true if the cap was hit (the walk
	/// stops early). Ignored directories are pruned, the rest recursed.
	/// </summary>
	private bool Walk(string directory, List<string> sink, int cap) {
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
					if (sink.Count >= cap) {
						return true;
					}

					sink.Add(fullPath);
				}
			}
		}

		return false;
	}
}
