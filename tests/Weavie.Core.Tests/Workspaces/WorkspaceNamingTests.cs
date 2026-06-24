using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary><see cref="WorkspaceNaming"/>: the leaf label, trailing-separator trimming, and the drive-root fallback.</summary>
public sealed class WorkspaceNamingTests {
	[Fact]
	public void Label_ReturnsLeafName() =>
		Assert.Equal("weavie", WorkspaceNaming.Label(Path.Combine("src", "weavie")));

	[Fact]
	public void Label_TrimsTrailingSeparator() =>
		Assert.Equal("weavie", WorkspaceNaming.Label(Path.Combine("src", "weavie") + Path.DirectorySeparatorChar));

	[Fact]
	public void Label_NoLeaf_FallsBackToRoot() {
		string root = Path.DirectorySeparatorChar.ToString();
		Assert.Equal(root, WorkspaceNaming.Label(root));
	}
}
