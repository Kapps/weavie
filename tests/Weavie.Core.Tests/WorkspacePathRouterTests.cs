using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The path → session routing decision behind <c>fs-stat</c>/<c>fs-read</c>/<c>fs-write</c>: the host must
/// route a file op to the session whose worktree contains the path, not the active session, or a switch
/// refuses the outgoing session's flush (lost edits) and turns a stale tab's read into a spurious not-found.
/// </summary>
public sealed class WorkspacePathRouterTests {
	private static string Root(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

	[Fact]
	public void PicksTheContainingRoot() {
		string a = Root("wt", "alpha");
		string b = Root("wt", "beta");

		Assert.Equal(1, WorkspacePathRouter.OwningRootIndex([a, b], Path.Combine(b, "src", "x.cs")));
		Assert.Equal(0, WorkspacePathRouter.OwningRootIndex([a, b], Path.Combine(a, "y.cs")));
	}

	[Fact]
	public void ReturnsMinusOneWhenNoRootContainsThePath() {
		string a = Root("wt", "alpha");
		Assert.Equal(-1, WorkspacePathRouter.OwningRootIndex([a], Root("elsewhere", "y.cs")));
	}

	[Fact]
	public void PrefersLongestPrefixWhenRootsNest() {
		// A nested root must win over its ancestor regardless of list order — today's worktrees never nest,
		// but ties are resolved deterministically rather than by position.
		string outer = Root("repo");
		string inner = Root("repo", "nested");
		string path = Path.Combine(inner, "z.cs");

		Assert.Equal(1, WorkspacePathRouter.OwningRootIndex([outer, inner], path));
		Assert.Equal(0, WorkspacePathRouter.OwningRootIndex([inner, outer], path));
	}

	[Fact]
	public void TheRootItselfIsOwned() {
		string a = Root("repo");
		Assert.Equal(0, WorkspacePathRouter.OwningRootIndex([a], a));
	}

	[Fact]
	public void IsCaseInsensitiveOnThePrefix() {
		// Mirrors BufferStore.IsWithinWorkspace (OrdinalIgnoreCase) — the Windows path semantics the editor uses.
		string a = Root("Repo");
		Assert.Equal(0, WorkspacePathRouter.OwningRootIndex([a], Path.Combine(Root("repo"), "x.cs")));
	}

	[Fact]
	public void EmptyInputsReturnMinusOne() {
		Assert.Equal(-1, WorkspacePathRouter.OwningRootIndex([], Root("x.cs")));
		Assert.Equal(-1, WorkspacePathRouter.OwningRootIndex([Root("a")], ""));
	}

	[Fact]
	public void ASiblingRootIsNotMistakenForAPrefix() {
		// "alpha" must not be treated as a prefix of "alpha-2" — the separator check guards this.
		string alpha = Root("wt", "alpha");
		string alpha2 = Root("wt", "alpha-2");
		Assert.Equal(1, WorkspacePathRouter.OwningRootIndex([alpha, alpha2], Path.Combine(alpha2, "x.cs")));
	}
}
