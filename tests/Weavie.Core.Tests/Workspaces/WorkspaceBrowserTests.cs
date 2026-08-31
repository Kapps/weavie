using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="WorkspaceBrowser"/>: directories-first ordering, listing a subdirectory by its returned
/// path, absolute entry paths, listing outside the root, and malformed/missing-directory failures.
/// </summary>
public sealed class WorkspaceBrowserTests {
	private static WorkspaceBrowser NewBrowser(params string[] files) {
		var seed = files.Select(f => new KeyValuePair<string, string>(f, "x"));
		return new WorkspaceBrowser(new InMemoryFileSystem(seed), "/proj");
	}

	[Fact]
	public void List_Root_ReturnsDirectoriesFirstThenFilesByName() {
		var browser = NewBrowser("/proj/src/main.cs", "/proj/src/util.cs", "/proj/readme.md", "/proj/.gitignore");

		var entries = browser.List(null);

		Assert.Equal(3, entries.Count);
		Assert.Equal("src", entries[0].Name);
		Assert.True(entries[0].IsDirectory);
		Assert.Equal([".gitignore", "readme.md"], entries.Skip(1).Select(e => e.Name));
		Assert.All(entries.Skip(1), e => Assert.False(e.IsDirectory));
	}

	[Fact]
	public void List_Subdirectory_ByReturnedPath() {
		var browser = NewBrowser("/proj/src/main.cs", "/proj/src/util.cs", "/proj/readme.md");
		var src = browser.List(null).First(e => e.Name == "src");

		var entries = browser.List(src.Path);

		Assert.Equal(["main.cs", "util.cs"], entries.Select(e => e.Name));
		Assert.All(entries, e => Assert.False(e.IsDirectory));
	}

	[Fact]
	public void List_EntryPathsAreAbsoluteUnderRoot() {
		var browser = NewBrowser("/proj/readme.md");

		var entry = Assert.Single(browser.List(null));

		Assert.Equal(Path.Combine(browser.Root, "readme.md"), entry.Path);
	}

	[Fact]
	public void List_PathOutsideTheRoot_ListsThatDirectory() {
		// Open-by-path completes directories anywhere; a relative request still resolves against the root.
		var browser = NewBrowser("/proj/readme.md", "/elsewhere/notes.md");

		Assert.Equal(["notes.md"], browser.List("/elsewhere").Select(e => e.Name));
		Assert.Equal(["notes.md"], browser.List("../elsewhere").Select(e => e.Name));
		Assert.Equal(["readme.md"], browser.List(null).Select(e => e.Name));
	}

	[Fact]
	public void List_MalformedPath_Throws() {
		var browser = NewBrowser("/proj/readme.md");

		Assert.ThrowsAny<ArgumentException>(() => browser.List("bad\0path"));
	}

	[Fact]
	public void List_MissingDirectory_Throws() {
		var browser = NewBrowser("/proj/readme.md");
		string ghost = Path.Combine(browser.Root, "does-not-exist");

		var error = Assert.Throws<DirectoryNotFoundException>(() => browser.List(ghost));
		Assert.Contains(ghost, error.Message, StringComparison.Ordinal);
	}
}
