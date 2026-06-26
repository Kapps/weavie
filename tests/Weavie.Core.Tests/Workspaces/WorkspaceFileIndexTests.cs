using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The recursive workspace file index backing the omnibar quick-open.</summary>
public sealed class WorkspaceFileIndexTests {
	// InMemoryFileSystem normalizes to full paths (drive letter on Windows); expectations must match.
	private static string Full(string p) => Path.GetFullPath(p);

	[Fact]
	public void List_WalksRecursively_ReturnsSortedAbsolutePaths() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/w/a.txt", "");
		fs.WriteAllText("/w/src/b.cs", "");
		fs.WriteAllText("/w/src/sub/c.ts", "");
		var index = new WorkspaceFileIndex(fs, "/w");

		var files = index.List();

		string[] expected = [Full("/w/a.txt"), Full("/w/src/b.cs"), Full("/w/src/sub/c.ts")];
		Array.Sort(expected, StringComparer.OrdinalIgnoreCase);
		Assert.Equal(expected, files);
	}

	[Fact]
	public void List_PrunesIgnoredDirectories() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/w/keep.cs", "");
		fs.WriteAllText("/w/node_modules/pkg/index.js", "");
		fs.WriteAllText("/w/.git/config", "");
		fs.WriteAllText("/w/bin/out.dll", "");
		var index = new WorkspaceFileIndex(fs, "/w");

		var files = index.List();

		Assert.Equal([Full("/w/keep.cs")], files);
	}

	[Fact]
	public void List_IsUnbounded_ReturnsEveryFile() {
		var fs = new InMemoryFileSystem();
		for (int i = 0; i < 25_000; i++) {
			fs.WriteAllText($"/w/f{i}.txt", "");
		}

		var index = new WorkspaceFileIndex(fs, "/w");

		Assert.Equal(25_000, index.List().Count);
	}

	[Fact]
	public void List_MissingRoot_IsEmpty() {
		var index = new WorkspaceFileIndex(new InMemoryFileSystem(), "/nope");

		Assert.Empty(index.List());
	}
}
