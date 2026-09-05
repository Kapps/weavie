using SysPath = System.IO.Path;

namespace Weavie.TestSupport;

/// <summary>
/// A uniquely named throwaway directory under the system temp path, owning both its creation and the
/// single cleanup contract every test shares: clear read-only attributes (git's object files, junctions
/// excluded), delete recursively, and tolerate a handle that outlives the test — most often on Windows.
/// </summary>
public sealed class TempDirectory : IDisposable {
	// Never recurse through a link: a temp tree may contain a junction or symlink to something precious.
	private static readonly EnumerationOptions Walk = new() {
		RecurseSubdirectories = true,
		AttributesToSkip = FileAttributes.ReparsePoint,
	};

	/// <summary>Creates a temp directory under a generic <c>weavie-</c> prefix.</summary>
	public TempDirectory() : this("weavie") {
	}

	/// <summary>Creates a temp directory whose name starts with <paramref name="prefix"/>.</summary>
	public TempDirectory(string prefix) {
		Path = SysPath.Combine(SysPath.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path);
	}

	/// <summary>The directory's absolute path.</summary>
	public string Path { get; }

	/// <summary>A path under this directory. Nothing is created.</summary>
	public string Combine(params string[] segments) => SysPath.Combine(Path, SysPath.Combine(segments));

	/// <summary>Creates a subdirectory (with any missing parents) and returns its path.</summary>
	public string CreateDirectory(params string[] segments) => Directory.CreateDirectory(Combine(segments)).FullName;

	/// <summary>Writes a file under this directory, creating any missing parents, and returns its path.</summary>
	public string WriteFile(string relativePath, string contents) {
		string path = Combine(relativePath);
		Directory.CreateDirectory(SysPath.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
		return path;
	}

	/// <summary>Deletes the directory and everything under it.</summary>
	public void Dispose() {
		try {
			foreach (string file in Directory.EnumerateFiles(Path, "*", Walk)) {
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(Path, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Best-effort: a watcher or child process can still hold the tree once the test is over.
		}
	}
}
