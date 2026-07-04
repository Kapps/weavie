using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The managed-layout detection both the runner and the worker share (docs/specs/runner-auto-update.md):
/// the layout root is found from a version dir, and a worker's hook-relay path is baked through the
/// <c>current</c> symlink so it outlives version-dir pruning.
/// </summary>
public sealed class ManagedRunnerLayoutTests {
	[Fact]
	public void RootContaining_FindsTheRootFromAWorkerBaseDir() {
		string root = Path.Combine(Path.GetTempPath(), "weavie-layout-" + Guid.NewGuid().ToString("n"));
		string workerDir = Path.Combine(root, "versions", "63", "worker");
		Directory.CreateDirectory(workerDir);
		try {
			Assert.Equal(root, ManagedRunnerLayout.RootContaining(workerDir));
			Assert.Null(ManagedRunnerLayout.RootContaining(Path.GetTempPath()));
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void CurrentRelayPath_ResolvesThroughCurrent_ForAManagedWorker() {
		string workerDir = Path.Combine(Path.GetTempPath(), "wv", "versions", "63", "worker");
		string expected = Path.Combine(Path.GetTempPath(), "wv", "current", "worker", "weavie-hook-relay");
		Assert.Equal(expected, ManagedRunnerLayout.CurrentRelayPath(workerDir, "weavie-hook-relay"));
	}

	[Fact]
	public void CurrentRelayPath_IsNull_OutsideAManagedLayout() =>
		Assert.Null(ManagedRunnerLayout.CurrentRelayPath(Path.GetTempPath(), "weavie-hook-relay"));

	[Fact]
	public void LoadedBuildNumber_ReadsTheBuildFromTheWorkerPath() {
		string workerDir = Path.Combine(Path.GetTempPath(), "wv", "versions", "114", "worker");
		Assert.Equal(114, ManagedRunnerLayout.LoadedBuildNumber(workerDir));
	}

	[Fact]
	public void LoadedBuildNumber_IsNull_OutsideAManagedLayout() =>
		Assert.Null(ManagedRunnerLayout.LoadedBuildNumber(Path.GetTempPath()));
}
