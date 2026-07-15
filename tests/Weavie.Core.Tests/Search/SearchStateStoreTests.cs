using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Search;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SearchStateStore"/> over the in-memory filesystem: defaults (gitignored excluded),
/// options + recent-terms persistence across reloads, MRU dedup/bound, no-op-on-unchanged (no rewrite, no
/// event), change notifications, and malformed-file backup + reset.
/// </summary>
public sealed class SearchStateStoreTests {
	private const string StorePath = "/weavie-search-state-tests/search-state.json";

	private static GrepOptions Options(bool caseSensitive, bool regex, string include) =>
		new() {
			CaseSensitive = caseSensitive,
			WholeWord = false,
			Regex = regex,
			ExcludeGitignored = true,
			Include = include,
			Exclude = "",
		};

	[Fact]
	public void Defaults_ExcludesGitignored_EmptyGlobsAndHistory() {
		var state = new SearchStateStore(new InMemoryFileSystem(), StorePath).Current;

		Assert.False(state.Options.CaseSensitive);
		Assert.True(state.Options.ExcludeGitignored); // the sensible default is ON
		Assert.Equal("", state.Options.Include);
		Assert.Empty(state.RecentTerms);
	}

	[Fact]
	public void SetOptions_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new SearchStateStore(fs, StorePath).SetOptions(Options(caseSensitive: true, regex: true, include: "*.ts"));

		var reloaded = new SearchStateStore(fs, StorePath).Current;

		Assert.True(reloaded.Options.CaseSensitive);
		Assert.True(reloaded.Options.Regex);
		Assert.Equal("*.ts", reloaded.Options.Include);
	}

	[Fact]
	public void AddRecentTerm_PersistsMostRecentFirst_AcrossReload() {
		var fs = new InMemoryFileSystem();
		var store = new SearchStateStore(fs, StorePath);
		store.AddRecentTerm("first");
		store.AddRecentTerm("second");

		var reloaded = new SearchStateStore(fs, StorePath).Current;

		Assert.Equal(["second", "first"], reloaded.RecentTerms);
	}

	[Fact]
	public void AddRecentTerm_DedupesToFront() {
		var store = new SearchStateStore(new InMemoryFileSystem(), StorePath);
		store.AddRecentTerm("a");
		store.AddRecentTerm("b");
		store.AddRecentTerm("a");

		Assert.Equal(["a", "b"], store.Current.RecentTerms);
	}

	[Fact]
	public void SetOptions_Unchanged_DoesNotRewriteOrNotify() {
		var fs = new InMemoryFileSystem();
		var store = new SearchStateStore(fs, StorePath);
		var options = Options(caseSensitive: true, regex: false, include: "src/");
		store.SetOptions(options);
		string afterFirst = fs.ReadAllText(StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetOptions(options);

		Assert.Equal(afterFirst, fs.ReadAllText(StorePath)); // identical bytes — no rewrite
		Assert.Equal(0, changes);
	}

	[Fact]
	public void AddRecentTerm_SameTopTerm_DoesNotNotify() {
		var store = new SearchStateStore(new InMemoryFileSystem(), StorePath);
		store.AddRecentTerm("a");
		int changes = 0;
		store.Changed += () => changes++;

		store.AddRecentTerm("a"); // already at the front — no change

		Assert.Equal(0, changes);
	}

	[Fact]
	public void Changed_FiresOnRealChange() {
		var store = new SearchStateStore(new InMemoryFileSystem(), StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetOptions(Options(caseSensitive: true, regex: false, include: ""));
		store.AddRecentTerm("hello");

		Assert.Equal(2, changes);
	}

	[Fact]
	public void HandEditedNulls_CoalesceInsteadOfThrowing() {
		// A hand edit that nulls a reference field is valid JSON (so the malformed-file guard doesn't fire) but
		// would throw out of the constructor without coalescing. The store must load sane defaults instead.
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, """{ "version": 1, "include": null, "recentTerms": null }""");

		var state = new SearchStateStore(fs, StorePath).Current;

		Assert.Equal("", state.Options.Include);
		Assert.Empty(state.RecentTerms);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var state = new SearchStateStore(fs, StorePath).Current;

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.True(state.Options.ExcludeGitignored);
		Assert.Empty(state.RecentTerms);
	}
}
