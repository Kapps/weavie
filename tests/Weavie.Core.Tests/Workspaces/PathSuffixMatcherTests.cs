using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="PathSuffixMatcher"/> recovers a loose file reference (a path missing its leading folders, or a
/// bare filename) by whole-segment suffix match over the workspace index — never matching inside a name.
/// </summary>
public sealed class PathSuffixMatcherTests {
	private static readonly string[] Files = [
		"/w/src/web/foo.ts",
		"/w/src/host/Program.cs",
		"/w/src/host/util/foo.ts",
		"/w/README.md",
	];

	[Fact]
	public void BareFilename_MatchesEveryFileWithThatName() =>
		Assert.Equal(["/w/src/web/foo.ts", "/w/src/host/util/foo.ts"], PathSuffixMatcher.Match(Files, "foo.ts"));

	[Fact]
	public void PathMissingLeadingFolders_MatchesOnWholeSegments() =>
		Assert.Equal(["/w/src/web/foo.ts"], PathSuffixMatcher.Match(Files, "web/foo.ts"));

	[Fact]
	public void NeverMatchesInsideASegment() {
		Assert.Empty(PathSuffixMatcher.Match(["/w/src/barfoo.ts"], "foo.ts")); // "…barfoo.ts" is not a foo.ts
		Assert.Empty(PathSuffixMatcher.Match(["/w/subweb/foo.ts"], "web/foo.ts"));
	}

	[Fact]
	public void ComparesIgnoreCase_AcrossEitherSeparator() {
		Assert.Equal(["/w/src/host/Program.cs"], PathSuffixMatcher.Match(Files, "program.cs"));
		Assert.Equal([@"C:\w\src\a.cs"], PathSuffixMatcher.Match([@"C:\w\src\a.cs"], "src/a.cs"));
	}

	[Fact]
	public void Normalize_DropsLeadingDotAndParentAndRootSegments() {
		Assert.Equal("web/foo.ts", PathSuffixMatcher.Normalize("./web/foo.ts"));
		Assert.Equal("web/foo.ts", PathSuffixMatcher.Normalize("../../web/foo.ts"));
		Assert.Equal("web/foo.ts", PathSuffixMatcher.Normalize("/web/foo.ts"));
		Assert.Equal("web/foo.ts", PathSuffixMatcher.Normalize(@"web\foo.ts"));
	}

	[Fact]
	public void EmptyOrDotOnlyReference_MatchesNothing() {
		Assert.Empty(PathSuffixMatcher.Match(Files, ""));
		Assert.Empty(PathSuffixMatcher.Match(Files, "../"));
	}

	[Fact]
	public void NormalizedReference_IsMatchedLikeTheRawOne() =>
		Assert.Equal(["/w/src/web/foo.ts"], PathSuffixMatcher.Match(Files, "../web/foo.ts"));
}
