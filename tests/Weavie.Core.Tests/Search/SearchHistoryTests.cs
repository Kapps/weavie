using Weavie.Core.Search;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the pure <see cref="SearchHistory"/> MRU rules.</summary>
public sealed class SearchHistoryTests {
	[Fact]
	public void Add_PrependsNewTerm() =>
		Assert.Equal(["b", "a"], SearchHistory.Add(["a"], "b"));

	[Fact]
	public void Add_PromotesExistingTermToFront_NoDuplicate() =>
		Assert.Equal(["a", "c", "b"], SearchHistory.Add(["c", "b", "a"], "a"));

	[Fact]
	public void Add_TrimsTerm() =>
		Assert.Equal(["b", "a"], SearchHistory.Add(["a"], "  b  "));

	[Fact]
	public void Add_EmptyOrWhitespaceTerm_LeavesListDeduped() {
		Assert.Equal(["a", "b"], SearchHistory.Add(["a", "b"], "   "));
		Assert.Equal(["a", "b"], SearchHistory.Add(["a", "a", "b"], ""));
	}

	[Fact]
	public void Add_DropsBlanksFromExisting() =>
		Assert.Equal(["b", "a"], SearchHistory.Add(["a", "  "], "b"));

	[Fact]
	public void Add_CapsAtMostRecent() {
		var seed = Enumerable.Range(0, SearchHistory.Cap).Select(i => $"t{i}").ToList();

		var result = SearchHistory.Add(seed, "newest");

		Assert.Equal(SearchHistory.Cap, result.Count);
		Assert.Equal("newest", result[0]);
		Assert.DoesNotContain($"t{SearchHistory.Cap - 1}", result); // the oldest fell off the end
	}
}
