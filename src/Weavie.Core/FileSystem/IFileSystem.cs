namespace Weavie.Core.FileSystem;

/// <summary>One entry in a directory listing: its leaf name (no path) and whether it is a subdirectory.</summary>
public readonly record struct DirectoryEntry(string Name, bool IsDirectory);

/// <summary>
/// A file's existence + metadata snapshot, used by the host-backed <c>file://</c> provider to build a
/// stat/etag for VSCode working copies. <paramref name="MtimeMs"/> and <paramref name="Size"/> together
/// form the conflict/etag check — both MUST change when the file's content changes.
/// </summary>
/// <param name="Exists">Whether the file or directory exists.</param>
/// <param name="IsDirectory">Whether the path is a directory.</param>
/// <param name="MtimeMs">Last-write time in milliseconds since the Unix epoch (0 when absent).</param>
/// <param name="CtimeMs">Creation time in milliseconds since the Unix epoch (0 when absent).</param>
/// <param name="Size">Size in bytes (0 for directories / absent).</param>
public readonly record struct FileStat(bool Exists, bool IsDirectory, long MtimeMs, long CtimeMs, long Size);

/// <summary>
/// The filesystem seam, injected so tests run in memory. One real impl (<see cref="LocalFileSystem"/>) and
/// one in-memory test fake (<see cref="InMemoryFileSystem"/>).
/// </summary>
public interface IFileSystem {
	/// <summary>Returns whether a file exists at <paramref name="path"/>.</summary>
	bool FileExists(string path);

	/// <summary>Returns whether a directory exists at <paramref name="path"/>.</summary>
	bool DirectoryExists(string path);

	/// <summary>
	/// Reads <paramref name="path"/>'s metadata into <paramref name="stat"/>, returning whether anything
	/// exists there. Never throws for a missing/denied path (returns <see langword="false"/> + default).
	/// </summary>
	bool TryGetStat(string path, out FileStat stat);

	/// <summary>
	/// Lists the immediate entries (files + subdirectories) of <paramref name="path"/>. Returns empty for a
	/// missing or unreadable directory — never throws.
	/// </summary>
	IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path);

	/// <summary>Reads the whole file as UTF-8 text. Throws if it does not exist.</summary>
	string ReadAllText(string path);

	/// <summary>Reads the whole file as raw bytes. Throws if it does not exist.</summary>
	byte[] ReadAllBytes(string path);

	/// <summary>Writes UTF-8 text, creating parent directories as needed, overwriting any existing file.</summary>
	void WriteAllText(string path, string contents);

	/// <summary>Writes raw bytes, creating parent directories as needed, overwriting any existing file.</summary>
	void WriteAllBytes(string path, byte[] contents);

	/// <summary>
	/// Writes UTF-8 text atomically (a crash leaves the old or new file, never a torn one), creating parent
	/// dirs. For app-managed config, not user source files — atomic-rename churns watchers and breaks ACLs there.
	/// </summary>
	void WriteAllTextAtomic(string path, string contents);

	/// <summary>
	/// Deletes the file at <paramref name="path"/>. A missing file is a no-op; a real delete error (ACLs, in
	/// use) propagates so the caller can surface it.
	/// </summary>
	void DeleteFile(string path);
}
