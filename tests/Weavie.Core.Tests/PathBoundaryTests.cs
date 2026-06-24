using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The one path-containment primitive behind every confinement guard (workspace, scratch, worktrees, themes,
/// the file browser). The adversarial cases (traversal, sibling prefix, UNC) are covered via
/// <see cref="IsWithinWorkspaceTests"/>; these pin the bits unique to the primitive: the root-inclusive
/// boundary and the case-sensitivity overload.
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
	public void Contains_EmptyInputs_AreNotContained() {
		Assert.False(PathBoundary.Contains("", Root));
		Assert.False(PathBoundary.Contains(Root, ""));
	}
}
