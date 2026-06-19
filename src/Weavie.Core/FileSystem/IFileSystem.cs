namespace Weavie.Core.FileSystem;

/// <summary>One entry in a directory listing: its leaf <paramref name="Name"/> (no path) and whether it is a subdirectory.</summary>
public readonly record struct DirectoryEntry(string Name, bool IsDirectory);

/// <summary>
/// A file's existence + metadata snapshot, used by the editor's host-backed <c>file://</c> provider to build
/// a stat/etag for VSCode working copies. <paramref name="MtimeMs"/> and <paramref name="Size"/> together are
/// the conflict/etag check — both MUST change when the file's content changes.
/// </summary>
/// <param name="Exists">Whether anything exists at the path.</param>
/// <param name="IsDirectory">Whether the path is a directory (vs. a file).</param>
/// <param name="MtimeMs">Last-write time in milliseconds since the Unix epoch (0 when absent).</param>
/// <param name="CtimeMs">Creation time in milliseconds since the Unix epoch (0 when absent).</param>
/// <param name="Size">Size in bytes (0 for directories / absent).</param>
public readonly record struct FileStat(bool Exists, bool IsDirectory, long MtimeMs, long CtimeMs, long Size);

/// <summary>
/// The filesystem seam. Injected so tests can run entirely in memory
/// (see the vault Build Philosophy + Headless &amp; Testing notes).
/// One real implementation (<see cref="LocalFileSystem"/>) and one
/// in-memory test fake (<see cref="InMemoryFileSystem"/>). No fallbacks.
/// </summary>
public interface IFileSystem {
	/// <summary>Returns whether a file exists at <paramref name="path"/>.</summary>
	bool FileExists(string path);

	/// <summary>Returns whether a directory exists at <paramref name="path"/>.</summary>
	bool DirectoryExists(string path);

	/// <summary>
	/// Tries to read <paramref name="path"/>'s metadata (existence, kind, mtime/ctime in ms since the Unix
	/// epoch, size). Returns <see langword="true"/> with a populated <paramref name="stat"/> when something
	/// exists there; <see langword="false"/> with a default <paramref name="stat"/> for a missing or
	/// unreadable path. Never throws for a missing/denied path.
	/// </summary>
	bool TryGetStat(string path, out FileStat stat);

	/// <summary>
	/// Lists the immediate entries (files + subdirectories) of <paramref name="path"/>. Returns empty when
	/// the directory does not exist or cannot be read — never throws for a missing/denied directory.
	/// </summary>
	IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path);

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
