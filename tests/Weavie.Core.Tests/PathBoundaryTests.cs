using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The one path-containment primitive behind every "untrusted path → privileged operation" boundary that
/// remains: archive extraction, worktree removal, theme includes, and the OSC 7 terminal cwd. These pin it
/// against the escape classes an attacker would actually try, since a regression here silently re-opens those
/// boundaries. Pure path math (no filesystem), so they isolate containment from any existence check.
/// </summary>
public sealed class PathBoundaryTests {
	private static string Root => Path.Combine(Path.GetTempPath(), "weavie-pb");

	[Fact]
	public void Contains_RootItselfAndDescendants_ButNotSiblings() {
		Assert.True(PathBoundary.Contains(Root, Root));                       // the root counts as contained
		Assert.True(PathBoundary.Contains(Root, Path.Combine(Root, "a/b")));  // a descendant
		Assert.False(PathBoundary.Contains(Root, Root + "-evil"));            // a sibling sharing the prefix
	}

	[Fact]
	public void Contains_DefaultComparison_IsCaseInsensitive() {
		string upper = Path.Combine(Path.GetTempPath(), "WEAVIE-PB", "x");
		Assert.True(PathBoundary.Contains(Root, upper));
	}

	[Fact]
	public void Contains_WithOrdinalComparison_IsCaseSensitive() {
		string upper = Path.Combine(Path.GetTempPath(), "WEAVIE-PB", "x");
		Assert.False(PathBoundary.Contains(Root, upper, StringComparison.Ordinal));
		Assert.True(PathBoundary.Contains(Root, Path.Combine(Root, "x"), StringComparison.Ordinal));
	}

	[Fact]
	public void Contains_TraversalEscapingTheRoot_IsRejected() =>
		Assert.False(PathBoundary.Contains(Root, Path.Combine(Root, "..", "evil", "a.cs")));

	[Fact]
	public void Contains_AbsolutePathOutsideTheRoot_IsRejected() =>
		Assert.False(PathBoundary.Contains(Root, Path.Combine(Path.GetTempPath(), "weavie-elsewhere", "a.cs")));

	[Fact]
	public void Contains_UncPath_IsRejectedAgainstALocalRoot() =>
		Assert.False(PathBoundary.Contains(Root, @"\\attacker\share\evil.exe"));

	[Fact]
	public void Contains_EmptyInputs_AreNotContained() {
		Assert.False(PathBoundary.Contains("", Root));
		Assert.False(PathBoundary.Contains(Root, ""));
	}
}
