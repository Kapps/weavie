using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SpellCheckerTests {
	private static SpellChecker Checker() => new(SpellCatalog.LoadEmbedded(), []);

	[Fact]
	public void EmbeddedLocales_LoadAndRecognizeCommonWords() {
		var catalog = SpellCatalog.LoadEmbedded();

		foreach (string locale in SpellLocales.Supported) {
			Assert.True(catalog.Check("the", locale, CancellationToken.None));
		}
	}

	[Fact]
	public void Check_SplitsIdentifiersAndKeepsUtf16Offsets() {
		var issues = Checker().Check(
			"🌱 recieveMessage", "typescript", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("recieve", issue.Word);
		Assert.Equal(3, issue.Start);
		Assert.Equal(7, issue.Length);
	}

	[Fact]
	public void Check_SkipsUrlsEmailsAcronymsAndDigitBearingTokens() {
		var issues = Checker().Check(
			"https://example.invalid/teh alpha@example.com HTTP version2 teh",
			"typescript", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
	}

	[Fact]
	public void Check_NormalizesCurlyApostrophesWithoutChangingReportedText() {
		var issues = Checker().Check(
			"don’t teh", "plaintext", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
	}

	[Fact]
	public void Suggest_ReturnsBoundedHunspellSuggestions() {
		var suggestions = Checker().Suggest("teh", SpellLocales.EnUs, CancellationToken.None);

		Assert.NotEmpty(suggestions);
		Assert.True(suggestions.Count <= 5);
		Assert.Contains("the", suggestions, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("kubernetes", "plaintext")]
	[InlineData("angletype", "typescript")]
	[InlineData("nint", "csharp")]
	[InlineData("gopath", "go")]
	public void Check_UsesEmbeddedSoftwareAndLanguageVocabulary(string word, string languageId) {
		var issues = Checker().Check(word, languageId, SpellLocales.EnUs, CancellationToken.None);

		Assert.Empty(issues);
	}
}
