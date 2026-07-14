using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the pure <see cref="GitGrep"/> layer: flag/pathspec mapping and the <c>-z --column</c> parser.</summary>
public sealed class GitGrepTests {
	private static GrepOptions Options() =>
		new() { CaseSensitive = false, WholeWord = false, Regex = false, Include = "", Exclude = "" };

	/// <summary>One <c>git grep -n --column -z</c> output record: <c>path NUL line NUL column NUL text NL</c>.</summary>
	private static string Row(string path, string line, string column, string text) =>
		$"{path}\0{line}\0{column}\0{text}\n";

	[Fact]
	public void BuildArgs_Defaults_LiteralCaseInsensitive_QueryStaysAnOperand() {
		// "-e <query> --" keeps an option-shaped query (leading '-') an operand, never parsed as a flag.
		var args = GitGrep.BuildArgs("-rf", Options());

		Assert.Equal(["grep", "-n", "--column", "-z", "-I", "--no-color", "--untracked", "-F", "-i", "-e", "-rf", "--"], args);
	}

	[Fact]
	public void BuildArgs_MapsCaseWordAndRegexToggles() {
		var args = GitGrep.BuildArgs("foo", Options() with { CaseSensitive = true, WholeWord = true, Regex = true });

		Assert.Equal(["grep", "-n", "--column", "-z", "-I", "--no-color", "--untracked", "-E", "-w", "-e", "foo", "--"], args);
	}

	[Fact]
	public void BuildArgs_AppendsIncludeThenExcludePathspecs() {
		var args = GitGrep.BuildArgs("foo", Options() with { Include = "src/**", Exclude = "dist/" });

		Assert.Equal([":(glob)src/**", ":(exclude,glob)dist/**"], args.Skip(args.ToList().IndexOf("--") + 1));
	}

	[Theory]
	[InlineData("*.ts", new[] { ":(glob)**/*.ts", ":(glob)**/*.ts/**" })]                 // bare glob: any depth, file or dir
	[InlineData("node_modules", new[] { ":(glob)**/node_modules", ":(glob)**/node_modules/**" })]
	[InlineData("docs/", new[] { ":(glob)docs/**" })]                                     // trailing '/': the whole directory
	[InlineData("src/**", new[] { ":(glob)src/**" })]                                     // has '/': root-anchored, as written
	[InlineData("./a/b.md", new[] { ":(glob)a/b.md" })]                                   // leading ./ and / are stripped
	[InlineData("/src/x.ts", new[] { ":(glob)src/x.ts" })]
	[InlineData(" *.md ,, docs/ ", new[] { ":(glob)**/*.md", ":(glob)**/*.md/**", ":(glob)docs/**" })] // trimmed, empties dropped
	[InlineData("", new string[0])]
	public void ExpandPathspecs_MapsTokensToGitPathspecs(string globs, string[] expected) =>
		Assert.Equal(expected, GitGrep.ExpandPathspecs(globs, "glob"));

	[Fact]
	public void Parse_ParsesNulDelimitedPathLineColumnPreview() {
		string sample =
			Row("src/a.ts", "12", "5", "const x = 1;")
			+ Row("src/a.ts", "30", "1", "import y;")
			+ Row("docs/b.md", "3", "9", "see foo");

		var result = GitGrep.Parse(sample, 500);

		Assert.False(result.Truncated);
		Assert.Equal(3, result.Matches.Count);
		Assert.Equal("src/a.ts", result.Matches[0].Path);
		Assert.Equal(12, result.Matches[0].Line);
		Assert.Equal(5, result.Matches[0].Column);
		Assert.Equal("const x = 1;", result.Matches[0].Preview);
		Assert.Equal("docs/b.md", result.Matches[2].Path);
	}

	[Fact]
	public void Parse_PreservesColonsInPathAndPreview() {
		// NUL delimiters make ':' plain content in both the path and the preview.
		var result = GitGrep.Parse(Row("src/we:ird.ts", "1", "13", "const url = \"http://x:8080\";"), 500);

		Assert.Single(result.Matches);
		Assert.Equal("src/we:ird.ts", result.Matches[0].Path);
		Assert.Equal("const url = \"http://x:8080\";", result.Matches[0].Preview);
	}

	[Fact]
	public void Parse_CapsAndFlagsTruncation() {
		string sample = Row("a", "1", "1", "x") + Row("a", "2", "1", "y") + Row("a", "3", "1", "z");

		var result = GitGrep.Parse(sample, 2);

		Assert.True(result.Truncated);
		Assert.Equal(2, result.Matches.Count);
	}

	[Fact]
	public void Parse_SkipsMalformedLines_TrimsCrLfPreviews() {
		string sample = Row("good", "1", "2", "hit\r") + "not-a-match-line\n" + Row("bad", "notnum", "1", "x");

		var result = GitGrep.Parse(sample, 500);

		Assert.Single(result.Matches);
		Assert.Equal("good", result.Matches[0].Path);
		Assert.Equal("hit", result.Matches[0].Preview);
		Assert.Equal(2, result.Matches[0].Column);
	}

	[Fact]
	public void Parse_Empty_ReturnsNoMatches() {
		var result = GitGrep.Parse(string.Empty, 500);

		Assert.Empty(result.Matches);
		Assert.False(result.Truncated);
	}

	[Fact]
	public void Parse_ConvertsByteColumnsToUtf16() {
		// "héllo x" — 'é' is 2 UTF-8 bytes, so a match on 'x' reports byte column 8 but UTF-16 column 7.
		var result = GitGrep.Parse(Row("f", "1", "8", "héllo x"), 500);

		Assert.Equal(7, result.Matches[0].Column);
	}

	[Theory]
	[InlineData("abc", 1, 1)]       // start of line
	[InlineData("abc", 3, 3)]       // ASCII: bytes == chars
	[InlineData("héllo", 4, 3)]     // 'é' = 2 bytes, 1 UTF-16 unit
	[InlineData("日本語x", 10, 4)]  // 3 CJK chars = 9 bytes, 3 UTF-16 units
	[InlineData("😀x", 5, 3)]       // emoji = 4 bytes, 2 UTF-16 units (surrogate pair)
	[InlineData("ab", 99, 3)]       // past the end clamps to line end
	public void Utf16Column_ConvertsByteOffsets(string line, int byteColumn, int expected) =>
		Assert.Equal(expected, GitGrep.Utf16Column(line, byteColumn));
}
