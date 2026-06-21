using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Owns a workspace's scratch (untitled-buffer) directory — <c>~/.weavie/workspaces/&lt;id&gt;/scratch</c>
/// (see <see cref="WeaviePaths.WorkspaceScratchDir"/>). A scratch file is a real file on disk so it reuses the
/// whole editor pipeline (a <c>file://</c> working copy that autosaves, restores across relaunch, and carries
/// view state), but it lives <em>outside</em> the workspace, so it never appears in the file tree, the index,
/// git, or Claude's view. <see cref="FileProviderService"/> treats this directory as a second allowed root.
/// The host creates one on <c>Ctrl+N</c>, deletes it when the tab is discarded or saved under a real name,
/// and garbage-collects orphans on launch.
/// </summary>
public sealed class ScratchStore {
	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();

	/// <summary>Creates a store over <paramref name="directory"/> (the workspace's scratch folder).</summary>
	public ScratchStore(IFileSystem fileSystem, string directory) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(directory);
		_fileSystem = fileSystem;
		Directory = directory;
	}

	/// <summary>The scratch directory this store manages.</summary>
	public string Directory { get; }

	/// <summary>
	/// Creates the next empty scratch file — <c>Untitled-1</c>, <c>Untitled-2</c>, … — using the lowest number
	/// not already on disk, and returns its absolute path. Extensionless so the editor defaults it to plain
	/// text; the user picks a real extension on save.
	/// </summary>
	public string CreateNew() {
		lock (_gate) {
			for (int n = 1; ; n++) {
				string path = Path.Combine(Directory, $"Untitled-{n}");
				if (!_fileSystem.FileExists(path)) {
					_fileSystem.WriteAllText(path, string.Empty);
					return path;
				}
			}
		}
	}

	/// <summary>True when <paramref name="path"/> resolves inside this store's scratch directory.</summary>
	public bool Owns(string path) => BufferStore.IsWithinWorkspace(Directory, path);

	/// <summary>
	/// Deletes a scratch file. Refuses paths outside the scratch directory; a missing file is a no-op. Returns
	/// whether anything was deleted.
	/// </summary>
	public bool Delete(string path) {
		if (!Owns(path) || !_fileSystem.FileExists(path)) {
			return false;
		}

		_fileSystem.DeleteFile(path);
		return true;
	}

	/// <summary>
	/// Deletes scratch files not in <paramref name="keep"/>. Run on launch so a buffer orphaned by a crash or
	/// reset session doesn't linger. Returns the number removed.
	/// </summary>
	/// <param name="keep">Absolute paths to retain (the session's open files).</param>
	public int GarbageCollect(IEnumerable<string> keep) {
		ArgumentNullException.ThrowIfNull(keep);
		lock (_gate) {
			if (!_fileSystem.DirectoryExists(Directory)) {
				return 0;
			}

			var keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string path in keep) {
				keepSet.Add(Path.GetFullPath(path));
			}

			int removed = 0;
			foreach (var entry in _fileSystem.EnumerateDirectory(Directory)) {
				if (entry.IsDirectory) {
					continue;
				}

				string full = Path.GetFullPath(Path.Combine(Directory, entry.Name));
				if (!keepSet.Contains(full)) {
					_fileSystem.DeleteFile(full);
					removed++;
				}
			}

			return removed;
		}
	}
}
