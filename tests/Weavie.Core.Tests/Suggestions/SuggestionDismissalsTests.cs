using Weavie.Core.FileSystem;
using Weavie.Core.Suggestions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SuggestionDismissals"/> over the in-memory filesystem: add/IsDismissed, persistence
/// across reloads, and malformed-file backup + reset.
/// </summary>
public sealed class SuggestionDismissalsTests {
	private const string StorePath = "/weavie-suggestion-dismissals/suggestions.json";

	[Fact]
	public void Add_ThenIsDismissed_True() {
		var store = new SuggestionDismissals(new InMemoryFileSystem(), StorePath);

		store.Add("worktree.setupCommand");

		Assert.True(store.IsDismissed("worktree.setupCommand"));
		Assert.False(store.IsDismissed("something.else"));
	}

	[Fact]
	public void Add_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new SuggestionDismissals(fs, StorePath).Add("worktree.setupCommand");

		Assert.True(new SuggestionDismissals(fs, StorePath).IsDismissed("worktree.setupCommand"));
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new SuggestionDismissals(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.False(store.IsDismissed("worktree.setupCommand"));
	}
}
