using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="WorkspacePaths"/>: the ignored-segment set (case-insensitive), whole-path segment scanning, and
/// the Windows drive-letter canonicalization (which must match the web's canonicalFsPath).
/// </summary>
public sealed class WorkspacePathsTests {
	[Theory]
	[InlineData("node_modules", true)]
	[InlineData("NODE_MODULES", true)]
	[InlineData(".git", true)]
	[InlineData("bin", true)]
	[InlineData("src", false)]
	public void IsIgnoredSegment_MatchesCaseInsensitively(string segment, bool ignored) =>
		Assert.Equal(ignored, WorkspacePaths.IsIgnoredSegment(segment));

	[Fact]
	public void HasIgnoredSegment_TrueWhenAnySegmentIsIgnored() =>
		Assert.True(WorkspacePaths.HasIgnoredSegment(Path.Combine("repo", "node_modules", "pkg", "index.js")));

	[Fact]
	public void HasIgnoredSegment_FalseWhenNoSegmentIsIgnored() =>
		Assert.False(WorkspacePaths.HasIgnoredSegment(Path.Combine("repo", "src", "app.cs")));

	[Fact]
	public void CanonicalFsPath_LowercasesLeadingDriveLetter() =>
		Assert.Equal(@"c:\src\app.cs", WorkspacePaths.CanonicalFsPath(@"C:\src\app.cs"));

	[Fact]
	public void CanonicalFsPath_NonDrivePath_Untouched() =>
		Assert.Equal("/Src/App.cs", WorkspacePaths.CanonicalFsPath("/Src/App.cs"));

	[Fact]
	public void CanonicalFsPath_NonLetterBeforeColon_Untouched() =>
		Assert.Equal("1:foo", WorkspacePaths.CanonicalFsPath("1:foo"));
}
