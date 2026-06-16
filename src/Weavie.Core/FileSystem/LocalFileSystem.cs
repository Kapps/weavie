using System.Text;

namespace Weavie.Core.FileSystem;

/// <summary>The real, on-disk filesystem implementation. UTF-8, no BOM.</summary>
public sealed class LocalFileSystem : IFileSystem {
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	/// <inheritdoc/>
	public bool FileExists(string path) => File.Exists(path);

	/// <inheritdoc/>
	public string ReadAllText(string path) => File.ReadAllText(path, Utf8NoBom);

	/// <inheritdoc/>
	public void WriteAllText(string path, string contents) {
		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, contents, Utf8NoBom);
	}
}
