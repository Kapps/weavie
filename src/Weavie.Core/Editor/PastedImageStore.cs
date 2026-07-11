using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// Owns a session's pasted-image directory (see <see cref="WeaviePaths.WorkspacePastedImagesDir"/>). An image
/// pasted into Claude is written here as <c>paste-N&lt;ext&gt;</c> and its path injected into the prompt, so the
/// files never reach the tree, index, or git. Wiped on session unload via <see cref="Clear"/>.
/// </summary>
public sealed class PastedImageStore {
	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();

	/// <summary>Creates a store over <paramref name="directory"/> (the session's pasted-images folder).</summary>
	public PastedImageStore(IFileSystem fileSystem, string directory) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(directory);
		_fileSystem = fileSystem;
		Directory = directory;
	}

	/// <summary>The pasted-images directory this store manages.</summary>
	public string Directory { get; }

	/// <summary>
	/// Writes <paramref name="bytes"/> to the next free <c>paste-N</c> file (lowest number not on disk) with
	/// <paramref name="extension"/>, and returns its absolute path.
	/// </summary>
	public string Write(string extension, byte[] bytes) {
		ArgumentException.ThrowIfNullOrEmpty(extension);
		ArgumentNullException.ThrowIfNull(bytes);
		lock (_gate) {
			for (int n = 1; ; n++) {
				string path = Path.Combine(Directory, $"paste-{n}{extension}");
				if (!_fileSystem.FileExists(path)) {
					_fileSystem.WriteAllBytes(path, bytes);
					return path;
				}
			}
		}
	}

	/// <summary>Deletes one image previously returned by <see cref="Write"/>. A missing file is a no-op.</summary>
	public void Delete(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			string fullPath = Path.GetFullPath(path);
			string root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			if (!fullPath.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
				throw new InvalidOperationException("Pasted image path is outside its session directory.");
			}

			if (_fileSystem.FileExists(fullPath)) {
				_fileSystem.DeleteFile(fullPath);
			}
		}
	}

	/// <summary>Deletes every pasted image in the directory (session unload). A missing directory is a no-op.</summary>
	public void Clear() {
		lock (_gate) {
			if (!_fileSystem.DirectoryExists(Directory)) {
				return;
			}

			foreach (var entry in _fileSystem.EnumerateDirectory(Directory)) {
				if (!entry.IsDirectory) {
					_fileSystem.DeleteFile(Path.Combine(Directory, entry.Name));
				}
			}
		}
	}
}
