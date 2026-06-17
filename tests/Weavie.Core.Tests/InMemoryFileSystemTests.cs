using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class InMemoryFileSystemTests {
	[Fact]
	public void WriteThenRead_RoundTrips() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/a/b.txt", "content");
		Assert.True(fs.FileExists("/a/b.txt"));
		Assert.Equal("content", fs.ReadAllText("/a/b.txt"));
	}

	[Fact]
	public void Read_Missing_Throws() {
		var fs = new InMemoryFileSystem();
		Assert.Throws<FileNotFoundException>(() => fs.ReadAllText("/nope"));
	}

	[Fact]
	public void Seed_PrepopulatesFiles() {
		var fs = new InMemoryFileSystem(new Dictionary<string, string> { ["/x"] = "seeded" });
		Assert.Equal("seeded", fs.ReadAllText("/x"));
	}

	[Fact]
	public void Paths_NormalizeToFullPath_SoRelativeAndAbsoluteCollide() {
		var fs = new InMemoryFileSystem();
		string absolute = Path.GetFullPath("relative.txt");
		fs.WriteAllText("relative.txt", "v1");
		fs.WriteAllText(absolute, "v2");
		Assert.Single(fs.Paths);
		Assert.Equal("v2", fs.ReadAllText("relative.txt"));
	}
}
