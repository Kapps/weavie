using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The recursive workspace file index backing the omnibar quick-open.</summary>
public sealed class WorkspaceFileIndexTests {
	// InMemoryFileSystem normalizes seed paths to full paths; expectations must too (drive letter on Windows).
	private static string Full(string p) => Path.GetFullPath(p);

	[Fact]
	public void List_WalksRecursively_ReturnsSortedAbsolutePaths() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/w/a.txt", "");
		fs.WriteAllText("/w/src/b.cs", "");
		fs.WriteAllText("/w/src/sub/c.ts", "");
		var index = new WorkspaceFileIndex(fs, "/w");

		var files = index.List(WorkspaceFileIndex.DefaultCap);

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

		var files = index.List(WorkspaceFileIndex.DefaultCap);

		Assert.Equal([Full("/w/keep.cs")], files);
	}

	[Fact]
	public void List_RespectsCap_AndLogsTruncation() {
		var fs = new InMemoryFileSystem();
		for (int i = 0; i < 5; i++) {
			fs.WriteAllText($"/w/f{i}.txt", "");
		}

		var index = new WorkspaceFileIndex(fs, "/w");
		string? logged = null;
		index.Log += m => logged = m;

		var files = index.List(cap: 3);

		Assert.Equal(3, files.Count);
		Assert.NotNull(logged);
	}

	[Fact]
	public void List_MissingRoot_IsEmpty() {
		var index = new WorkspaceFileIndex(new InMemoryFileSystem(), "/nope");

		Assert.Empty(index.List(WorkspaceFileIndex.DefaultCap));
	}
}
