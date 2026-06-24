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

	private static string Abs(string relative) => Path.GetFullPath(relative);

	[Fact]
	public void TryGetStat_File_ReportsFileWithByteSize() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Abs("dir/a.txt"), "héllo"); // 'é' is 2 UTF-8 bytes → 6 bytes

		Assert.True(fs.TryGetStat(Abs("dir/a.txt"), out var stat));
		Assert.True(stat.Exists);
		Assert.False(stat.IsDirectory);
		Assert.Equal(6, stat.Size);
	}

	[Fact]
	public void TryGetStat_Directory_ReportsDirectory() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Abs("dir/a.txt"), "x");

		Assert.True(fs.TryGetStat(Abs("dir"), out var stat));
		Assert.True(stat.Exists);
		Assert.True(stat.IsDirectory);
		Assert.Equal(0, stat.Size);
	}

	[Fact]
	public void TryGetStat_Missing_ReturnsFalse() {
		var fs = new InMemoryFileSystem();
		Assert.False(fs.TryGetStat(Abs("nope"), out var stat));
		Assert.False(stat.Exists);
	}

	[Fact]
	public void TryGetStat_MtimeAdvancesOnRewrite() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Abs("a.txt"), "v1");
		Assert.True(fs.TryGetStat(Abs("a.txt"), out var first));
		fs.WriteAllText(Abs("a.txt"), "v2");
		Assert.True(fs.TryGetStat(Abs("a.txt"), out var second));

		Assert.True(second.MtimeMs > first.MtimeMs);
	}

	[Fact]
	public void DirectoryExists_TrueOnlyForAncestorOfAFile() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Abs("dir/a.txt"), "x");

		Assert.True(fs.DirectoryExists(Abs("dir")));
		Assert.False(fs.DirectoryExists(Abs("other")));
	}

	[Fact]
	public void EnumerateDirectory_SeparatesFilesFromSubdirectories() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Abs("dir/a.txt"), "a");
		fs.WriteAllText(Abs("dir/b.txt"), "b");
		fs.WriteAllText(Abs("dir/sub/c.txt"), "c");

		var entries = fs.EnumerateDirectory(Abs("dir"));

		Assert.Contains(entries, e => e is { Name: "a.txt", IsDirectory: false });
		Assert.Contains(entries, e => e is { Name: "b.txt", IsDirectory: false });
		Assert.Contains(entries, e => e is { Name: "sub", IsDirectory: true });
		Assert.DoesNotContain(entries, e => e.Name == "c.txt"); // nested, not an immediate entry
	}

	[Fact]
	public void DeleteFile_RemovesFile_MissingIsNoOp() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Abs("a.txt"), "x");

		fs.DeleteFile(Abs("a.txt"));
		Assert.False(fs.FileExists(Abs("a.txt")));
		fs.DeleteFile(Abs("a.txt")); // second delete is a no-op, not a throw
	}
}
