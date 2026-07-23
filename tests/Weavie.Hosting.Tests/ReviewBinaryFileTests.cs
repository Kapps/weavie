using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Text review arming when git also reports paths the editor cannot safely decode.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class ReviewBinaryFileTests {
	[Fact]
	public async Task DiffAgainstHead_SkipsBinaryPathsAndArmsEveryTextChange() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllBytes(Path.Combine(repo, "modified.bin"), [0, 1, 2]);
			File.WriteAllBytes(Path.Combine(repo, "deleted.bin"), [0xc3, 0x28]);
			Commit(repo, "binary fixtures");
		});
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nreview me\n");
		File.WriteAllBytes(Path.Combine(host.RepoRoot, "modified.bin"), [0, 3, 4]);
		File.Delete(Path.Combine(host.RepoRoot, "deleted.bin"));
		File.WriteAllBytes(Path.Combine(host.RepoRoot, "untracked.bin"), [0xff, 0]);

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		var files = changes.GetProperty("files").EnumerateArray().ToList();
		var text = Assert.Single(files);
		Assert.Equal("readme.txt", text.GetProperty("name").GetString());
		Assert.Equal("vs HEAD", changes.GetProperty("label").GetString());
	}

	[Fact]
	public async Task DiffAgainstHead_WithOnlyBinaryChanges_LeavesNoEmptyReviewArmed() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllBytes(Path.Combine(repo, "asset.bin"), [0, 1, 2]);
			Commit(repo, "binary fixture");
		});
		File.WriteAllBytes(Path.Combine(host.RepoRoot, "asset.bin"), [0, 3, 4]);
		host.Bridge.Clear();

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Contains("No text changes", toast.GetProperty("message").GetString());
		Assert.Null(host.Core.ActiveSessionForTest()!.Changes.ActiveReviewIdentity);
	}

	private static void Commit(string repo, string message) {
		TestHost.RunGit(repo, "add", "-A");
		TestHost.RunGit(
			repo,
			"-c", "user.email=test@weavie.dev",
			"-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false",
			"commit", "--quiet", "-m", message);
	}
}
