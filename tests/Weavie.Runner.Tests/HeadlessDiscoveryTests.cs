using Xunit;

namespace Weavie.Runner.Tests;

public sealed class HeadlessDiscoveryTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-worker-discovery");
	private static string Apphost => OperatingSystem.IsWindows() ? "Weavie.Headless.exe" : "Weavie.Headless";

	public void Dispose() => _dir.Dispose();

	[Theory]
	[InlineData("", false)]
	[InlineData("", true)]
	[InlineData("worker", false)]
	[InlineData("worker", true)]
	public void DiscoversBothWorkerFormsInBothBundleLayouts(string subdirectory, bool native) {
		string worker = _dir.WriteFile(Path.Combine(subdirectory, native ? Apphost : "Weavie.Headless.dll"), "");

		var (path, error) = RunnerOptions.ResolveHeadlessPath(null, _dir.Path);

		Assert.Null(error);
		Assert.Equal(worker, path);
	}

	[Theory]
	[InlineData("")]
	[InlineData("worker")]
	public void PrefersApphostWhenBothFormsExist(string subdirectory) {
		_dir.WriteFile(Path.Combine(subdirectory, "Weavie.Headless.dll"), "");
		string worker = _dir.WriteFile(Path.Combine(subdirectory, Apphost), "");

		Assert.Equal(worker, RunnerOptions.ResolveHeadlessPath(null, _dir.Path).Path);
	}

	[Fact]
	public void PrefersBundledWorkerOverRootColocation() {
		_dir.WriteFile(Apphost, "");
		string worker = _dir.WriteFile(Path.Combine("worker", "Weavie.Headless.dll"), "");

		Assert.Equal(worker, RunnerOptions.ResolveHeadlessPath(null, _dir.Path).Path);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DevProbeRewritesOnlyTheLastRunnerSegment(bool native) {
		string baseDir = _dir.CreateDirectory("Weavie.Runner", "src", "Weavie.Runner", "bin", "Debug", "net10.0")
			+ Path.DirectorySeparatorChar;
		string sibling = _dir.WriteFile(Path.Combine("Weavie.Runner", "src", "Weavie.Headless", "bin", "Debug", "net10.0",
			native ? Apphost : "Weavie.Headless.dll"), "");
		File.WriteAllText(Path.Combine(baseDir, Apphost), "");

		Assert.Equal(sibling, RunnerOptions.ResolveHeadlessPath(null, baseDir).Path);
	}

	[Theory]
	[InlineData("Weavie.Headless")]
	[InlineData("Weavie.Headless.exe")]
	[InlineData("Weavie.Headless.dll")]
	public void ExplicitPathOverridesDiscovery(string name) {
		_dir.WriteFile(Path.Combine("worker", Apphost), "");
		string explicitPath = _dir.WriteFile(Path.Combine("custom", name), "");

		Assert.Equal(explicitPath, RunnerOptions.ResolveHeadlessPath(explicitPath, _dir.Path).Path);
	}

	[Fact]
	public void MissingExplicitWorkerDoesNotUseDiscoveredWorker() {
		_dir.WriteFile(Path.Combine("worker", Apphost), "");
		var (path, error) = RunnerOptions.ResolveHeadlessPath(_dir.Combine("missing"), _dir.Path);

		Assert.Null(path);
		Assert.Contains("does not exist", error);
	}

	[Fact]
	public void MissingWorkerReportsAllProbesAndBothSupportedForms() {
		var (path, error) = RunnerOptions.ResolveHeadlessPath(null, _dir.Path);

		Assert.Null(path);
		Assert.Contains(_dir.Combine("worker", Apphost), error);
		Assert.Contains(_dir.Combine("worker", "Weavie.Headless.dll"), error);
		Assert.Contains(_dir.Combine(Apphost), error);
		Assert.Contains(_dir.Combine("Weavie.Headless.dll"), error);
		Assert.Contains("--headless <path>", error);
	}
}
