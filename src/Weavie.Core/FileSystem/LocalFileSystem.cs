using System.Text;

namespace Weavie.Core.FileSystem;

/// <summary>The real, on-disk filesystem implementation. UTF-8, no BOM.</summary>
public sealed class LocalFileSystem : IFileSystem {
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	/// <inheritdoc/>
	public bool FileExists(string path) => File.Exists(path);

	/// <inheritdoc/>
	public bool DirectoryExists(string path) => Directory.Exists(path);

	/// <inheritdoc/>
	public bool TryGetStat(string path, out FileStat stat) {
		try {
			if (File.Exists(path)) {
				var info = new FileInfo(path);
				stat = new FileStat(true, false, ToUnixMs(info.LastWriteTimeUtc), ToUnixMs(info.CreationTimeUtc), info.Length);
				return true;
			}

			if (Directory.Exists(path)) {
				var info = new DirectoryInfo(path);
				stat = new FileStat(true, true, ToUnixMs(info.LastWriteTimeUtc), ToUnixMs(info.CreationTimeUtc), 0);
				return true;
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
			// A path we can't stat (delete race, ACLs, malformed) reports as absent rather than throwing.
		}

		stat = default;
		return false;
	}

	private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

	/// <inheritdoc/>
	public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) {
		if (!Directory.Exists(path)) {
			return [];
		}

		try {
			var entries = new List<DirectoryEntry>();
			foreach (string dir in Directory.EnumerateDirectories(path)) {
				entries.Add(new DirectoryEntry(Path.GetFileName(dir), true));
			}

			foreach (string file in Directory.EnumerateFiles(path)) {
				entries.Add(new DirectoryEntry(Path.GetFileName(file), false));
			}

			return entries;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// A directory we can't enter (ACLs) returns empty rather than crashing the browser.
			return [];
		}
	}

	/// <inheritdoc/>
	public string ReadAllText(string path) => File.ReadAllText(path, Utf8NoBom);

	/// <inheritdoc/>
	public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

	/// <inheritdoc/>
	public void WriteAllText(string path, string contents) {
		string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, contents, Utf8NoBom);
	}

	/// <inheritdoc/>
	public void WriteAllBytes(string path, byte[] contents) {
		string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		File.WriteAllBytes(path, contents);
	}

	/// <inheritdoc/>
	public void AppendAllText(string path, string contents) {
		string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		File.AppendAllText(path, contents, Utf8NoBom);
	}

	/// <inheritdoc/>
	public void WriteAllTextAtomic(string path, string contents) {
		string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		// Swap a sibling temp file into place: File.Replace is atomic and preserves the destination's
		// attributes/ACLs; File.Move covers the first-write case where there's no target.
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, contents, Utf8NoBom);
		if (File.Exists(path)) {
			File.Replace(tmp, path, null);
		} else {
			File.Move(tmp, path);
		}
	}

	/// <inheritdoc/>
	public void DeleteFile(string path) {
		try {
			File.Delete(path);
		} catch (DirectoryNotFoundException) {
			// Containing directory is gone, so the file is too — treat as already absent.
		}
	}
}
