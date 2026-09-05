using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.FileSystem;

/// <summary>
/// The one "is this the same path" primitive, previously eleven hand-rolled copies that disagreed on whether
/// the caller or the helper applied <see cref="Path.GetFullPath(string)"/>, on trailing separators, and on the
/// case rule. These pin both axes and the platform case rule, since a regression silently merges two distinct
/// worktrees or strands a session whose path is spelled differently.
/// </summary>
public sealed class PathIdentityTests {
	private static string Root => Path.Combine(Path.GetTempPath(), "weavie-pi");

	[Fact]
	public void Normalize_TrimsTrailingSeparator_AndCanonicalizes() {
		Assert.Equal(Root, PathIdentity.Normalize(Root + Path.DirectorySeparatorChar));
		Assert.Equal(Root, PathIdentity.Normalize(Path.Combine(Root, "a", "..")));
	}

	[Fact]
	public void Normalize_KeepsTheFilesystemRootIntact() {
		string root = Path.GetPathRoot(Path.GetFullPath(Root))!;
		Assert.Equal(root, PathIdentity.Normalize(root));
	}

	[Fact]
	public void Normalize_ResolvesRelativePathsAgainstABase() =>
		Assert.Equal(Path.Combine(Root, "a"), PathIdentity.Normalize("a", Root));

	[Fact]
	public void Equals_NormalizesBothSides() {
		Assert.True(PathIdentity.Equals(Root + Path.DirectorySeparatorChar, Root));
		Assert.True(PathIdentity.Equals(Path.Combine(Root, "a", ".."), Root));
		Assert.False(PathIdentity.Equals(Root, Root + "-other"));
	}

	[Fact]
	public void CaseRule_FollowsThePlatform() {
		string upper = Path.Combine(Path.GetTempPath(), "WEAVIE-PI");
		Assert.Equal(OperatingSystem.IsWindows(), PathIdentity.Equals(Root, upper));
		Assert.Equal(OperatingSystem.IsWindows(), PathIdentity.Comparer.Equals(Root, upper));
	}

	[Fact]
	public void Comparer_NormalizesKeys_SoACallerCannotForget() {
		var set = new HashSet<string>(PathIdentity.Comparer) { Root + Path.DirectorySeparatorChar };
		Assert.Contains(Root, set);
		Assert.Contains(Path.Combine(Root, "a", ".."), set);
		Assert.DoesNotContain(Root + "-other", set);
	}

	[Fact]
	public void Comparer_HashAgreesWithEquals() {
		Assert.Equal(
			PathIdentity.Comparer.GetHashCode(Root),
			PathIdentity.Comparer.GetHashCode(Root + Path.DirectorySeparatorChar));
		var byPath = new Dictionary<string, int>(PathIdentity.Comparer) { [Root] = 1 };
		byPath[Root + Path.DirectorySeparatorChar] = 2;
		Assert.Equal(2, Assert.Single(byPath).Value);
	}

	[Fact]
	public void Comparer_TreatsNullAsItsOwnValue() {
		Assert.True(PathIdentity.Comparer.Equals(null, null));
		Assert.False(PathIdentity.Comparer.Equals(null, Root));
	}
}
