namespace Weavie.Core.FileSystem;

/// <summary>
/// The filesystem seam. Injected so tests can run entirely in memory
/// (see the vault Build Philosophy + Headless &amp; Testing notes).
/// One real implementation (<see cref="LocalFileSystem"/>) and one
/// in-memory test fake (<see cref="InMemoryFileSystem"/>). No fallbacks.
/// </summary>
public interface IFileSystem {
	/// <summary>Returns whether a file exists at <paramref name="path"/>.</summary>
	bool FileExists(string path);

	/// <summary>Reads the whole file as UTF-8 text. Throws if it does not exist.</summary>
	string ReadAllText(string path);

	/// <summary>Writes UTF-8 text, creating parent directories as needed, overwriting any existing file.</summary>
	void WriteAllText(string path, string contents);
}
