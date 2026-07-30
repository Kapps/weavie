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
			"🌱 recieveMessage", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("recieve", issue.Word);
		Assert.Equal(3, issue.Start);
		Assert.Equal(7, issue.Length);
	}

	[Fact]
	public void Check_ReportsOffsetsAcrossTheWholeDocument() {
		var issues = Checker().Check(
			"first line\nsecond teh", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
		Assert.Equal(18, issue.Start);
	}

	[Fact]
	public void Check_SkipsUrlsEmailsAcronymsAndDigitBearingTokens() {
		var issues = Checker().Check(
			"https://example.invalid/teh alpha@example.com HTTP version2 teh",
			SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
	}

	[Fact]
	public void Check_NormalizesCurlyApostrophesWithoutChangingReportedText() {
		var issues = Checker().Check(
			"don’t teh", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
	}

	[Fact]
	public void Check_RecognizesStraightAndCurlyPluralPossessives() {
		var issues = Checker().Check(
			"James' users' James’ users’ teh", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
	}

	[Fact]
	public void Check_KeepsDecomposedCombiningMarksInWordRanges() {
		string directory = Path.Combine(Path.GetTempPath(), "weavie-spelling-tests", Guid.NewGuid().ToString("N"));
		string dictionaryPath = Path.Combine(directory, "dictionary.txt");
		const string known = "za\u0338z";
		const string unknown = "zz\u0338zz";
		try {
			using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);
			dictionary.Add(known);
			var checker = new SpellChecker(SpellCatalog.LoadEmbedded(), [dictionary]);

			var issues = checker.Check($"{known} {unknown}", SpellLocales.EnUs, CancellationToken.None);

			var issue = Assert.Single(issues);
			Assert.Equal(unknown, issue.Word);
			Assert.Equal(known.Length + 1, issue.Start);
			Assert.Equal(unknown.Length, issue.Length);
		} finally {
			if (Directory.Exists(directory)) {
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	[Fact]
	public void Check_DoesNotCountCombiningMarksTowardMinimumWordLength() {
		var issues = Checker().Check(
			"a\u0338b teh", SpellLocales.EnUs, CancellationToken.None);

		var issue = Assert.Single(issues);
		Assert.Equal("teh", issue.Word);
	}

	[Fact]
	public void Check_DoesNotStartAWordWithACombiningMarkAfterASeparator() {
		var issues = Checker().Check(
			"foo_\u0301bar teh", SpellLocales.EnUs, CancellationToken.None);

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
	[InlineData("kubernetes")]
	[InlineData("angletype")]
	[InlineData("nint")]
	[InlineData("gopath")]
	public void Check_UsesCombinedEmbeddedSoftwareVocabulary(string word) {
		var issues = Checker().Check(word, SpellLocales.EnUs, CancellationToken.None);

		Assert.Empty(issues);
	}
}
