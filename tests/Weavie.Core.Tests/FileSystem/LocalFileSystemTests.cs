using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.FileSystem;

public sealed class LocalFileSystemTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), "weavie-filesystem-test-" + Guid.NewGuid().ToString("n"));

	[Fact]
	public void TryReadAllText_DistinguishesUtf8TextFromBinary() {
		Directory.CreateDirectory(_root);
		string path = Path.Combine(_root, "file");
		var fileSystem = new LocalFileSystem();
		File.WriteAllText(path, "héllo 🌍\n");

		Assert.True(fileSystem.TryReadAllText(path, out string text));
		Assert.Equal("héllo 🌍\n", text);

		File.WriteAllBytes(path, [0x50, 0x4b, 0x00, 0xff]);
		Assert.False(fileSystem.TryReadAllText(path, out text));
		Assert.Equal(string.Empty, text);
	}

	public void Dispose() {
		if (Directory.Exists(_root)) {
			Directory.Delete(_root, recursive: true);
		}
	}
}
