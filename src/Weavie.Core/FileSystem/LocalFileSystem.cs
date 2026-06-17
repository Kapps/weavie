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
			// A directory we can list the parent of but not enter (ACLs) shouldn't crash the browser.
			return [];
		}
	}

	/// <inheritdoc/>
	public string ReadAllText(string path) => File.ReadAllText(path, Utf8NoBom);

	/// <inheritdoc/>
	public void WriteAllText(string path, string contents) {
		string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, contents, Utf8NoBom);
	}

	/// <inheritdoc/>
	public void WriteAllTextAtomic(string path, string contents) {
		string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		// Write to a sibling temp file then swap into place: File.Replace is atomic and preserves the
		// destination's attributes/ACLs; File.Move covers the first-write case where there's no target.
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, contents, Utf8NoBom);
		if (File.Exists(path)) {
			File.Replace(tmp, path, null);
		} else {
			File.Move(tmp, path);
		}
	}
}
