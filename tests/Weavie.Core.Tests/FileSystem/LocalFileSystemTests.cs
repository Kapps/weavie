using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.FileSystem;

public sealed class LocalFileSystemTests {
	[Fact]
	public void TryReadAllText_DistinguishesUtf8TextFromBinary() {
		using var root = new TempDirectory("weavie-filesystem-test");
		string path = root.Combine("file");
		var fileSystem = new LocalFileSystem();
		File.WriteAllText(path, "héllo 🌍\n");

		Assert.True(fileSystem.TryReadAllText(path, out string text));
		Assert.Equal("héllo 🌍\n", text);

		File.WriteAllBytes(path, [0x50, 0x4b, 0x00, 0xff]);
		Assert.False(fileSystem.TryReadAllText(path, out text));
		Assert.Equal(string.Empty, text);
	}
}
