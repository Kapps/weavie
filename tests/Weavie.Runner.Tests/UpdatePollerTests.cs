using Xunit;

namespace Weavie.Runner.Tests;

public sealed class UpdatePollerTests {
	[Fact]
	public void OlderBuildsAreIgnoredBeforeTheCurrentContractIsEnforced() {
		Assert.Equal(UpdateCandidateDisposition.Older,
			UpdatePoller.ClassifyCandidate(Manifest(99, RunnerIdentity.SpawnContract - 1), 100));
		Assert.Equal(UpdateCandidateDisposition.ContractMismatch,
			UpdatePoller.ClassifyCandidate(Manifest(100, RunnerIdentity.SpawnContract - 1), 100));
		Assert.Equal(UpdateCandidateDisposition.Current,
			UpdatePoller.ClassifyCandidate(Manifest(100, RunnerIdentity.SpawnContract), 100));
		Assert.Equal(UpdateCandidateDisposition.Newer,
			UpdatePoller.ClassifyCandidate(Manifest(101, RunnerIdentity.SpawnContract), 100));
	}

	private static BundleManifest Manifest(int build, int spawnContract) =>
		new() { BuildNumber = build, SpawnContract = spawnContract };
}
