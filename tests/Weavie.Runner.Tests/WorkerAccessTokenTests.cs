using Xunit;

namespace Weavie.Runner.Tests;

public sealed class WorkerAccessTokenTests {
	[Fact]
	public void Derivation_has_a_versioned_known_vector() {
		string workspace = OperatingSystem.IsWindows() ? @"C:\srv\weavie\project" : "/srv/weavie/project";
		string expected = OperatingSystem.IsWindows()
			? "e13047d3871783de4e959eddd512663a"
			: "c0f9e2ccdbc66577b72937b8e877b878";

		Assert.Equal(expected, WorkerAccessToken.Derive("0123456789abcdef0123456789abcdef", workspace));
	}

	[Fact]
	public void Derivation_is_stable_across_trailing_separators() {
		string workspace = Path.Combine(Path.GetTempPath(), "weavie", "workspace");

		Assert.Equal(
			WorkerAccessToken.Derive("runner", workspace),
			WorkerAccessToken.Derive("runner", workspace + Path.DirectorySeparatorChar));
	}

	[Fact]
	public void Derivation_rotates_with_the_runner_or_workspace() {
		string workspace = Path.Combine(Path.GetTempPath(), "weavie", "workspace");
		string token = WorkerAccessToken.Derive("runner-a", workspace);

		Assert.Matches("^[0-9a-f]{32}$", token);
		Assert.NotEqual(token, WorkerAccessToken.Derive("runner-b", workspace));
		Assert.NotEqual(token, WorkerAccessToken.Derive("runner-a", workspace + "-other"));
	}

	[Fact]
	public void Windows_workspace_identity_is_case_insensitive() {
		if (!OperatingSystem.IsWindows()) {
			return;
		}

		Assert.Equal(
			WorkerAccessToken.Derive("runner", @"C:\Workspace\Weavie"),
			WorkerAccessToken.Derive("runner", @"c:/workspace/weavie"));
	}
}
