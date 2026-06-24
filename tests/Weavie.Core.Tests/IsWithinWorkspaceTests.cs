using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="BufferStore.IsWithinWorkspace"/> is the path-containment guard behind every "untrusted path →
/// privileged operation" boundary — file revert/write and the OSC 7 terminal cwd. These pin it against the
/// escape classes an attacker would actually try, since a regression here silently re-opens those boundaries.
/// Pure path math (no filesystem), so they isolate the containment decision from any existence check.
/// </summary>
public sealed class IsWithinWorkspaceTests {
	private static string Root => Path.Combine(Path.GetTempPath(), "weavie-ws");

	[Fact]
	public void ContainedPath_IsWithin() =>
		Assert.True(BufferStore.IsWithinWorkspace(Root, Path.Combine(Root, "src", "a.cs")));

	[Fact]
	public void TheRootItself_IsWithin() => Assert.True(BufferStore.IsWithinWorkspace(Root, Root));

	[Fact]
	public void Traversal_EscapingTheRoot_IsRejected() =>
		Assert.False(BufferStore.IsWithinWorkspace(Root, Path.Combine(Root, "..", "evil", "a.cs")));

	[Fact]
	public void AbsolutePathOutsideTheRoot_IsRejected() =>
		Assert.False(BufferStore.IsWithinWorkspace(Root, Path.Combine(Path.GetTempPath(), "weavie-elsewhere", "a.cs")));

	[Fact]
	public void SiblingWithTheRootAsAStringPrefix_IsRejected() {
		// "weavie-ws" must not count as a prefix of "weavie-ws-evil" — the directory-separator check guards this,
		// and it's the classic StartsWith path-confinement bug.
		Assert.False(BufferStore.IsWithinWorkspace(Root, Root + "-evil"));
		Assert.False(BufferStore.IsWithinWorkspace(Root, Path.Combine(Root + "-evil", "a.cs")));
	}

	[Fact]
	public void UncPath_IsRejectedAgainstALocalRoot() =>
		Assert.False(BufferStore.IsWithinWorkspace(Root, @"\\attacker\share\evil.exe"));

	[Fact]
	public void EmptyInputs_AreRejected() {
		Assert.False(BufferStore.IsWithinWorkspace("", Root));
		Assert.False(BufferStore.IsWithinWorkspace(Root, ""));
	}

	[Fact]
	public void CaseInsensitivePrefix_IsWithin() {
		// The guard is OrdinalIgnoreCase by design (the editor's Windows path semantics), independent of the OS.
		string root = Path.Combine(Path.GetTempPath(), "Weavie-Case");
		Assert.True(BufferStore.IsWithinWorkspace(root, Path.Combine(Path.GetTempPath(), "weavie-case", "a.cs")));
	}
}
