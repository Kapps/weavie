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

	/// <summary>
	/// Writes UTF-8 text atomically (a crash leaves either the old or the new file, never a torn one),
	/// creating parent directories as needed. For app-managed config documents; not for user source
	/// files, where atomic-rename has observable costs (file-watcher churn, broken hardlinks, lost ACLs).
	/// </summary>
	void WriteAllTextAtomic(string path, string contents);
}
