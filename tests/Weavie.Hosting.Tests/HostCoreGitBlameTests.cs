using System.Text.Json;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The blame surface as the editor reaches it: over the session bus, addressing files by absolute path. Covers
/// what the Core git tests can't — the path resolution at that boundary, and the forge links behind a commit.
/// </summary>
public sealed class HostCoreGitBlameTests {
	private const string PullRequestUrl = "https://github.com/kapps/weavie/pull/42";

	[Fact]
	public async Task BlameAnswersEachLineAndItsHunkForAFileInTheWorktree() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nbravo\ncharlie\n");
			Commit(repo, "seed notes");
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nBRAVO\ncharlie\n");
			Commit(repo, "shout the second line");
		});
		string path = Path.Combine(host.RepoRoot, "notes.md");

		var blame = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "blame", new { path });

		Assert.Equal(JsonValueKind.Null, blame.GetProperty("error").ValueKind);
		var commits = blame.GetProperty("commits").EnumerateArray().ToList();
		var lineCommits = blame.GetProperty("lineCommits").EnumerateArray().Select(l => l.GetInt32()).ToList();
		Assert.Equal(3, lineCommits.Count);
		Assert.Equal(
			"shout the second line",
			commits[lineCommits[1]].GetProperty("summary").GetString());
		Assert.NotEqual(lineCommits[0], lineCommits[1]);

		// The blamed line's number inside its commit is what pulls up the change it came from.
		int originalLine = blame.GetProperty("lineOriginals").EnumerateArray().ElementAt(1).GetInt32();
		var hunk = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession,
			"git",
			"commitHunk",
			new { path, sha = commits[lineCommits[1]].GetProperty("sha").GetString(), line = originalLine });

		Assert.Contains(
			"+BRAVO",
			hunk.GetProperty("hunk").GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
	}

	[Fact]
	public async Task HistoryListsTheCommitsBehindTheLineAndTheFile() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nbravo\n");
			Commit(repo, "first");
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nBRAVO\n");
			Commit(repo, "second");
			File.WriteAllText(Path.Combine(repo, "notes.md"), "ALPHA\nBRAVO\n");
			Commit(repo, "third");
		});
		string path = Path.Combine(host.RepoRoot, "notes.md");

		var lineHistory = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "history", new { path, line = 2 });
		var fileHistory = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "history", new { path, line = 0 });

		// Line 2 was written by "second" and untouched by "third"; the file itself carries all three.
		Assert.Equal(["second", "first"], Summaries(lineHistory));
		Assert.Equal(["third", "second", "first"], Summaries(fileHistory));
		Assert.False(lineHistory.GetProperty("more").GetBoolean());
		// A line-scoped entry carries where the line sat in that commit, so its hunk can be pulled up too.
		Assert.Equal(2, lineHistory.GetProperty("commits").EnumerateArray().First().GetProperty("line").GetInt32());
		// A file-scoped entry carries no line: the commit touched the file, not necessarily this line.
		Assert.Equal(0, fileHistory.GetProperty("commits").EnumerateArray().First().GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task APathOutsideTheWorktreeIsRefusedRatherThanReachingGit() {
		await using var host = await TestHost.StartAsync(_ => { });
		string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "secret.md");

		var blame = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "blame", new { path = outside });

		Assert.Contains("isn't inside", blame.GetProperty("error").GetString());
		Assert.Empty(blame.GetProperty("commits").EnumerateArray());
	}

	[Fact]
	public async Task AShaThatIsNotACommitIsRefused() {
		await using var host = await TestHost.StartAsync(repo =>
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\n"));

		var hunk = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession,
			"git",
			"commitHunk",
			new { path = Path.Combine(host.RepoRoot, "notes.md"), sha = "HEAD", line = 1 });

		Assert.Equal(JsonValueKind.Null, hunk.GetProperty("hunk").ValueKind);
		Assert.Contains("isn't a commit", hunk.GetProperty("error").GetString());
	}

	[Fact]
	public async Task CommitRefLinksToTheForgeCommitAndThePullRequestBehindIt() {
		var provider = new StaticPullRequestProvider(
			[
				new PullRequestSummary {
					Number = 42,
					Title = "Shout the second line",
					Author = "kapps",
					HeadRef = "feature",
					BaseRef = "main",
					Url = PullRequestUrl,
					IsDraft = false,
					State = PullRequestState.Merged,
				},
			],
			[]);
		string sha = string.Empty;
		await using var host = await TestHost.StartAsync(
			repo => {
				File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\n");
				Commit(repo, "seed");
				TestHost.RunGit(repo, "remote", "add", "origin", "https://github.com/kapps/weavie.git");
				sha = ReadHead(repo);
				provider.PullRequestsByCommit[sha] = 42;
			},
			provider);

		var refs = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "commitRef", new { sha });

		Assert.Equal(
			$"https://github.com/kapps/weavie/commit/{sha}",
			refs.GetProperty("commitUrl").GetString());
		Assert.Equal(42, refs.GetProperty("pullRequest").GetProperty("number").GetInt32());
		Assert.Equal(PullRequestUrl, refs.GetProperty("pullRequest").GetProperty("url").GetString());
	}

	[Fact]
	public async Task ACommitWithNoPullRequestStillLinksToTheCommit() {
		string sha = string.Empty;
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\n");
			Commit(repo, "seed");
			TestHost.RunGit(repo, "remote", "add", "origin", "https://github.com/kapps/weavie.git");
			sha = ReadHead(repo);
		});

		var refs = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "commitRef", new { sha });

		Assert.EndsWith(sha, refs.GetProperty("commitUrl").GetString());
		Assert.Equal(JsonValueKind.Null, refs.GetProperty("pullRequest").ValueKind);
	}

	private static IEnumerable<string?> Summaries(JsonElement history) =>
		history.GetProperty("commits").EnumerateArray().Select(c => c.GetProperty("summary").GetString());

	private static void Commit(string repo, string message) {
		TestHost.RunGit(repo, "add", "-A");
		TestHost.RunGit(
			repo,
			"-c", "user.email=test@weavie.dev",
			"-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false",
			"commit", "--quiet", "-m", message);
	}

	private static string ReadHead(string repo) =>
		File.ReadAllText(Path.Combine(repo, ".git", "refs", "heads", "main")).Trim();
}
