using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreGitStatusTests {
	[Fact]
	public async Task StatusPollTracksManualCommitAndExternalNonLanguageEdit() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "readme.txt"), "replacement\nsecond\n");
			File.WriteAllText(Path.Combine(repo, "untracked.txt"), "not in git diff\n");
			TestHost.RunGit(repo, "add", "readme.txt");
		});

		await Wait.UntilAsync(() => HasCounts(host, 2, 1));

		var latest = host.Bridge.LastEvent(host.PrimarySession.Address, "git", "status")!.Value;
		Assert.Equal("main", latest.GetProperty("branch").GetString());
		Assert.True(latest.GetProperty("dirty").GetBoolean());

		TestHost.RunGit(
			host.RepoRoot,
			"-c", "user.email=test@weavie.dev",
			"-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false",
			"commit", "--quiet", "-m", "manual shell commit");
		await Wait.UntilAsync(() => HasCounts(host, 0, 0));

		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "external edit\n");
		await Wait.UntilAsync(() => HasCounts(host, 1, 2));
	}

	private static bool HasCounts(TestHost host, int added, int removed) =>
		host.Bridge.LastEvent(host.PrimarySession.Address, "git", "status") is { } status
		&& status.GetProperty("added").ValueKind == System.Text.Json.JsonValueKind.Number
		&& status.GetProperty("removed").ValueKind == System.Text.Json.JsonValueKind.Number
		&& status.GetProperty("added").GetInt32() == added
		&& status.GetProperty("removed").GetInt32() == removed;
}
