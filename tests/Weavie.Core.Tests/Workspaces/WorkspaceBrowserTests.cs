using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="WorkspaceBrowser"/> over the in-memory filesystem: directories-first ordering,
/// listing a subdirectory by its returned path, absolute entry paths, clamping escape attempts to the
/// root, and empty results for a missing directory. Also covers the new <see cref="IFileSystem"/>
/// enumeration the browser relies on.
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
	public void List_EscapeAttempt_ClampsToRoot() {
		// A file outside the root must never appear; the request falls back to listing the root.
		var browser = NewBrowser("/proj/readme.md", "/secret/passwords.txt");

		var entries = browser.List("../secret");

		Assert.DoesNotContain(entries, e => e.Name == "passwords.txt");
		Assert.Contains(entries, e => e.Name == "readme.md");
	}

	[Fact]
	public void List_MissingDirectory_IsEmpty() {
		var browser = NewBrowser("/proj/readme.md");
		string ghost = Path.Combine(browser.Root, "does-not-exist");

		Assert.Empty(browser.List(ghost));
	}
}
