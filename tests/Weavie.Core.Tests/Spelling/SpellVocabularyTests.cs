using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SpellVocabularyTests {
	[Theory]
	[InlineData("color colour center centre organize organise traveler traveller cheque check")]
	[InlineData("colors colours colored coloured coloring colouring organizing organising organized organised travelers travellers")]
	[InlineData("backend frontend middleware namespace nullable serializer serializers codebase whitespace autocomplete")]
	[InlineData("onboarding discoverability observability JSON async mutex bool enum const init args config params")]
	[InlineData("OAuth OAuthToken JSONs APIs JSONsCount TypeScript JavaScript GitHub iPhone macOS")]
	public void MixedEnglishAcceptsRegionalAndTechnicalWords(string text) {
		Assert.Empty(SpellChecker.Check(SpellVocabulary.English.Value, [new(0, 0, text, false)],
			new HashSet<string>(), new HashSet<string>(), CancellationToken.None));
	}

	[Theory]
	[InlineData("teh")]
	[InlineData("recieve")]
	[InlineData("mispelled")]
	[InlineData("middlewre")]
	[InlineData("namespcae")]
	[InlineData("ZZTYPOOs")]
	public void BroaderVocabularyStillRejectsTypos(string word) {
		Assert.Equal([new Misspelling(0, 0, word)], SpellChecker.Check(SpellVocabulary.English.Value,
			[new(0, 0, word, false)], new HashSet<string>(), new HashSet<string>(), CancellationToken.None));
	}

	[Fact]
	public void WholeWordsRespectCustomCasingBeforeIdentifierSplitting() {
		Assert.Empty(SpellChecker.Check(SpellVocabulary.English.Value,
			[new(0, 0, "ZorpBlarg", true), new(1, 0, "ZorpBlarg QuuxZorp", false)],
			new HashSet<string>(["zorpblarg"], StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(["quuxzorp"], StringComparer.OrdinalIgnoreCase), CancellationToken.None));
	}

	[Fact]
	public void AcronymPluralsPreserveTypoOffsetsInsideIdentifiers() {
		Assert.Equal([new Misspelling(4, 10, "Mispelled"), new Misspelling(4, 30, "ZZTYPOOs")],
			SpellChecker.Check(SpellVocabulary.English.Value, [new(4, 2, "😀 JSONsMispelled APIsCount ZZTYPOOs", false)],
				new HashSet<string>(), new HashSet<string>(), CancellationToken.None));
	}

	[Fact]
	public void SuggestionsIncludeTechnicalCorrectionsWithoutDuplicates() {
		string[] suggestions = [.. SpellVocabulary.English.Value.Suggest("middlewre")];
		Assert.Contains("middleware", suggestions);
		Assert.Equal(suggestions.Distinct(StringComparer.Ordinal), suggestions);
		Assert.Contains("misspelled", SpellVocabulary.English.Value.Suggest("mispelled"));
	}
}
