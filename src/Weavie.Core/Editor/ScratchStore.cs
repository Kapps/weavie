using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Owns a workspace's scratch (untitled-buffer) directory — <c>~/.weavie/workspaces/&lt;id&gt;/scratch</c>
/// (see <see cref="WeaviePaths.WorkspaceScratchDir"/>). A scratch file is real on disk so it reuses the whole
/// editor pipeline, but lives <em>outside</em> the workspace, so it never appears in the tree, index, git, or
/// Claude's view (<see cref="FileProviderService"/> treats it as a second allowed root).
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
	/// Creates the next empty <c>Untitled-N</c> scratch file (lowest number not on disk) and returns its absolute
	/// path. Extensionless so the editor defaults it to plain text; the user picks a real extension on save.
	/// </summary>
	public string CreateNew() => CreateNew(string.Empty, string.Empty);

	/// <summary>
	/// Creates the next empty <c>Untitled-N&lt;extension&gt;</c> scratch file seeded with <paramref name="content"/>
	/// and returns its absolute path — used to surface fetched read-only content (e.g. a Notion doc) as a normal
	/// editor tab. Pass an empty <paramref name="extension"/> for the plain-text default.
	/// </summary>
	public string CreateNew(string extension, string content) {
		ArgumentNullException.ThrowIfNull(extension);
		ArgumentNullException.ThrowIfNull(content);
		lock (_gate) {
			for (int n = 1; ; n++) {
				string path = Path.Combine(Directory, $"Untitled-{n}{extension}");
				if (!_fileSystem.FileExists(path)) {
					_fileSystem.WriteAllText(path, content);
					return path;
				}
			}
		}
	}

	/// <summary>True when <paramref name="path"/> resolves inside this store's scratch directory.</summary>
	public bool Owns(string path) => BufferStore.IsWithinWorkspace(Directory, path);

	/// <summary>Deletes a scratch file. Refuses paths outside the directory; a missing file is a no-op. Returns whether it deleted.</summary>
	public bool Delete(string path) {
		if (!Owns(path) || !_fileSystem.FileExists(path)) {
			return false;
		}

		_fileSystem.DeleteFile(path);
		return true;
	}

	/// <summary>
	/// Deletes scratch files not in <paramref name="keep"/>, run on launch so a crash-orphaned buffer doesn't
	/// linger. Returns the number removed.
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
