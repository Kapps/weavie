using System.Formats.Tar;
using System.IO.Compression;
using Xunit;

namespace Weavie.Runner.Tests;

/// <summary>
/// The managed version layout (docs/specs/runner-auto-update.md): staging flips <c>current</c> and
/// persists state; rollback restores the confirmed-good build and blacklists the bad digest; confirm
/// prunes superseded version dirs; a release tarball round-trips through <see cref="VersionStore.ExtractBundle"/>.
/// </summary>
public sealed class VersionStoreTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), "weavie-versions-" + Guid.NewGuid().ToString("n"));

	public void Dispose() {
		try {
			Directory.Delete(_root, recursive: true);
		} catch (IOException) {
			// best-effort temp cleanup
		}
	}

	[Fact]
	public void Open_AdoptsTheCurrentBuildFromAReleaseLayoutWithoutState() {
		InstallVersion(100);
		PointCurrentAt(100);

		var store = VersionStore.OpenAt(_root, _ => { });

		Assert.Equal(100, store.StagedBuild);
		Assert.Equal(100, VersionStore.OpenAt(_root, _ => { }).StagedBuild);
	}

	[Fact]
	public void Open_ReconcilesStateToCurrentAfterAnInterruptedSelectorSwap() {
		var store = VersionStore.OpenAt(_root, _ => { });
		store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 1 }, MakeExtractedVersion(100), "sha256:aa");
		store.MarkConfirmedGood(100);
		InstallVersion(101);
		PointCurrentAt(101);

		var reopened = VersionStore.OpenAt(_root, _ => { });

		Assert.Equal(101, reopened.StagedBuild);
		Assert.Equal(100, reopened.ConfirmedGoodBuild);
		Assert.False(reopened.IsKnownDigest("sha256:aa"));
	}

	[Fact]
	public void Stage_AdoptsAnExistingMatchingVersionWithoutReplacingIt() {
		string existing = InstallVersion(100);
		string worker = Path.Combine(existing, "worker", "Weavie.Headless.dll");
		File.WriteAllText(worker, "live");
		string downloaded = MakeExtractedVersion(100);
		var store = VersionStore.OpenAt(_root, _ => { });

		store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 1 }, downloaded, "sha256:aa");

		Assert.Equal("live", File.ReadAllText(worker));
		Assert.True(Directory.Exists(downloaded));
		Assert.Equal(100, store.StagedBuild);
	}

	[Fact]
	public void Stage_RejectsAnExistingBuildWithDifferentIdentityWithoutMutatingIt() {
		string existing = InstallVersion(100);
		string worker = Path.Combine(existing, "worker", "Weavie.Headless.dll");
		File.WriteAllText(worker, "live");
		string downloaded = MakeExtractedVersion(100);
		var store = VersionStore.OpenAt(_root, _ => { });

		Assert.Throws<InvalidDataException>(() =>
			store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 2 }, downloaded, "sha256:aa"));
		Assert.Equal("live", File.ReadAllText(worker));
		Assert.Null(store.StagedBuild);
		Assert.Null(new FileInfo(Path.Combine(_root, "current")).LinkTarget);
	}

	[Fact]
	public void RecordStagedDigest_RejectsDifferentContentForTheSameBuild() {
		InstallVersion(100);
		PointCurrentAt(100);
		var store = VersionStore.OpenAt(_root, _ => { });

		store.RecordStagedDigest(100, "sha256:aa");
		store.RecordStagedDigest(100, "sha256:aa");

		Assert.Throws<InvalidDataException>(() => store.RecordStagedDigest(100, "sha256:bb"));
		Assert.True(store.IsKnownDigest("sha256:aa"));
		Assert.False(store.IsKnownDigest("sha256:bb"));
	}

	[Fact]
	public void Open_RejectsADanglingCurrentSelector() {
		Directory.CreateDirectory(_root);
		PointCurrentAt(100);

		Assert.Throws<InvalidDataException>(() => VersionStore.OpenAt(_root, _ => { }));
	}

	[Fact]
	public void Open_RejectsCurrentOutsideTheManagedVersionsDirectory() {
		string outside = MakeExtractedVersion(100);
		Directory.CreateDirectory(_root);
		Directory.CreateSymbolicLink(Path.Combine(_root, "current"), outside);

		Assert.Throws<InvalidDataException>(() => VersionStore.OpenAt(_root, _ => { }));
	}

	[Fact]
	public void Stage_FlipsCurrent_AndPersistsAcrossReopen() {
		var store = VersionStore.OpenAt(_root, _ => { });
		store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 1 }, MakeExtractedVersion(100), "sha256:aa");

		Assert.Equal(100, store.StagedBuild);
		Assert.True(store.IsKnownDigest("sha256:aa"));
		string current = Path.Combine(_root, "current");
		Assert.Equal(Path.Combine("versions", "100"), new FileInfo(current).LinkTarget);
		Assert.Equal(Path.Combine(_root, "versions", "100", "worker", "Weavie.Headless.dll"), store.ActiveWorkerPath());
		Assert.Null(new FileInfo(current + ".new").LinkTarget);

		// A fresh open (a restarted runner) reads the same state back from disk.
		var reopened = VersionStore.OpenAt(_root, _ => { });
		Assert.Equal(100, reopened.StagedBuild);
		Assert.True(reopened.IsKnownDigest("sha256:aa"));
	}

	[Fact]
	public void Rollback_RestoresConfirmedGood_AndBlacklistsTheBadDigest() {
		var store = VersionStore.OpenAt(_root, _ => { });
		store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 1 }, MakeExtractedVersion(100), "sha256:aa");
		store.MarkConfirmedGood(100);
		store.Stage(new BundleManifest { BuildNumber = 101, SpawnContract = 1 }, MakeExtractedVersion(101), "sha256:bb");

		Assert.Equal(100, store.RollbackToConfirmed());
		Assert.Equal(100, store.StagedBuild);
		Assert.Equal(Path.Combine("versions", "100"), new FileInfo(Path.Combine(_root, "current")).LinkTarget);
		// The bad build is never retried, even by a restarted runner.
		Assert.True(VersionStore.OpenAt(_root, _ => { }).IsKnownDigest("sha256:bb"));
	}

	[Fact]
	public void Rollback_WithNothingConfirmed_ReturnsNull() {
		var store = VersionStore.OpenAt(_root, _ => { });
		store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 1 }, MakeExtractedVersion(100), "sha256:aa");
		Assert.Null(store.RollbackToConfirmed());
	}

	[Fact]
	public void ConfirmingABuild_PrunesSupersededVersions() {
		var store = VersionStore.OpenAt(_root, _ => { });
		store.Stage(new BundleManifest { BuildNumber = 100, SpawnContract = 1 }, MakeExtractedVersion(100), "sha256:aa");
		store.MarkConfirmedGood(100);
		store.Stage(new BundleManifest { BuildNumber = 101, SpawnContract = 1 }, MakeExtractedVersion(101), "sha256:bb");
		store.MarkConfirmedGood(101);

		Assert.False(Directory.Exists(Path.Combine(_root, "versions", "100")));
		Assert.True(Directory.Exists(Path.Combine(_root, "versions", "101")));
	}

	[Fact]
	public void ExtractBundle_RoundTripsTheReleaseTarball() {
		// The exact shape the release workflow packages: versions/<N>/… plus a current symlink.
		string layout = Path.Combine(_root, "layout");
		string versionDir = Path.Combine(layout, "versions", "247");
		Directory.CreateDirectory(Path.Combine(versionDir, "worker"));
		File.WriteAllText(Path.Combine(versionDir, "manifest.json"), """{ "buildNumber": 247, "spawnContract": 1 }""");
		File.WriteAllText(Path.Combine(versionDir, "worker", "Weavie.Headless.dll"), "bin");
		File.CreateSymbolicLink(Path.Combine(layout, "current"), Path.Combine("versions", "247"));

		string tarball = Path.Combine(_root, "bundle.tar.gz");
		using (var file = File.Create(tarball))
		using (var gzip = new GZipStream(file, CompressionMode.Compress)) {
			TarFile.CreateFromDirectory(layout, gzip, includeBaseDirectory: false);
		}

		var (manifest, extractedDir) = VersionStore.ExtractBundle(tarball, Path.Combine(_root, "scratch"));
		Assert.Equal(247, manifest.BuildNumber);
		Assert.Equal(1, manifest.SpawnContract);
		Assert.True(File.Exists(Path.Combine(extractedDir, "worker", "Weavie.Headless.dll")));
	}

	[Fact]
	public void ExtractBundle_WithoutManifest_Throws() {
		string layout = Path.Combine(_root, "layout");
		Directory.CreateDirectory(Path.Combine(layout, "versions", "1"));
		string tarball = Path.Combine(_root, "bundle.tar.gz");
		using (var file = File.Create(tarball))
		using (var gzip = new GZipStream(file, CompressionMode.Compress)) {
			TarFile.CreateFromDirectory(layout, gzip, includeBaseDirectory: false);
		}

		Assert.Throws<InvalidDataException>(() => VersionStore.ExtractBundle(tarball, Path.Combine(_root, "scratch")));
	}

	[Theory]
	[InlineData("0.1.247", 247)]
	[InlineData("0.1.0", 0)]
	public void ParseBuild_ReadsThePatchComponent(string identity, int expected) =>
		Assert.Equal(expected, RunnerIdentity.ParseBuild(identity));

	[Fact]
	public void ParseBuild_RejectsANonNumericPatch() =>
		Assert.Throws<FormatException>(() => RunnerIdentity.ParseBuild("0.1.abc"));

	// An extracted bundle dir as ExtractBundle would leave it, ready for Stage (which moves it).
	private string MakeExtractedVersion(int build) {
		string dir = Path.Combine(_root, "extracted", Guid.NewGuid().ToString("n"), build.ToString());
		Directory.CreateDirectory(Path.Combine(dir, "worker"));
		File.WriteAllText(Path.Combine(dir, "worker", "Weavie.Headless.dll"), "bin");
		File.WriteAllText(Path.Combine(dir, "manifest.json"), $$"""{ "buildNumber": {{build}}, "spawnContract": 1 }""");
		return dir;
	}

	private string InstallVersion(int build) {
		string target = Path.Combine(_root, "versions", build.ToString());
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		Directory.Move(MakeExtractedVersion(build), target);
		return target;
	}

	private void PointCurrentAt(int build) {
		string current = Path.Combine(_root, "current");
		if (new FileInfo(current).LinkTarget is not null) {
			File.Delete(current);
		}

		Directory.CreateSymbolicLink(current, Path.Combine("versions", build.ToString()));
	}
}
