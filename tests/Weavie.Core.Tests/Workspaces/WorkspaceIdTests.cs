using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="WorkspaceId"/>: normalization (trailing separators, relative vs absolute, case folding on
/// Windows) collapses equivalent paths to one id, distinct paths differ, and the value is a short
/// folder-safe hex digest.
/// </summary>
public sealed class WorkspaceIdTests {
	private static string Temp(string leaf) => Path.Combine(Path.GetTempPath(), "weavie-wsid-tests", leaf);

	[Fact]
	public void ForPath_TrailingSeparator_SameId() {
		string dir = Temp("proj");
		Assert.Equal(WorkspaceId.ForPath(dir), WorkspaceId.ForPath(dir + Path.DirectorySeparatorChar));
	}

	[Fact]
	public void ForPath_RelativeAndAbsolute_SameId() {
		var fromRelative = WorkspaceId.ForPath("weavie-wsid-rel");
		var fromAbsolute = WorkspaceId.ForPath(Path.GetFullPath("weavie-wsid-rel"));
		Assert.Equal(fromRelative, fromAbsolute);
	}

	[Fact]
	public void ForPath_DifferentPaths_DifferentIds() =>
		Assert.NotEqual(WorkspaceId.ForPath(Temp("alpha")), WorkspaceId.ForPath(Temp("beta")));

	[Fact]
	public void ForPath_ProducesShortHexValue() {
		var id = WorkspaceId.ForPath(Temp("proj"));
		Assert.Equal(16, id.Value.Length);
		Assert.Matches("^[0-9a-f]+$", id.Value);
	}

	[Fact]
	public void ForPath_CaseInsensitiveOnWindows() {
		if (!OperatingSystem.IsWindows()) {
			return; // case folding is Windows-only, matching its case-insensitive filesystem
		}

		Assert.Equal(WorkspaceId.ForPath(@"C:\Temp\Weavie\Proj"), WorkspaceId.ForPath(@"c:\temp\weavie\proj"));
	}
}
