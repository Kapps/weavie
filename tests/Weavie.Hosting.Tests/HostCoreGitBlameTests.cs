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
		string second = string.Empty;
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nbravo\n");
			Commit(repo, "first");
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nBRAVO\n");
			Commit(repo, "second");
			second = ReadHead(repo);
			File.WriteAllText(Path.Combine(repo, "notes.md"), "ALPHA\nBRAVO\n");
			Commit(repo, "third");
		});
		string path = Path.Combine(host.RepoRoot, "notes.md");

		// The line walk is addressed the way blame reports it: the commit that wrote the line, and its number
		// in that commit.
		var lineHistory = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "history", new { path, sha = second, line = 2 });
		var fileHistory = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "history", new { path, sha = string.Empty, line = 0 });

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
	public async Task LineHistoryAnswersAboutTheBlamedLineWhileTheFileHasUncommittedChanges() {
		// The editor asks about a line of a file the agent is midway through editing — the normal state here.
		// Blame's numbering is the working tree's, so the walk has to be anchored at the commit blame named.
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nbravo\ncharlie\n");
			Commit(repo, "first");
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nBRAVO\ncharlie\n");
			Commit(repo, "second");
			// Uncommitted: the tree is now longer than HEAD, and "BRAVO" has moved down two lines.
			File.WriteAllText(Path.Combine(repo, "notes.md"), "new\nlines\nalpha\nBRAVO\ncharlie\n");
		});
		string path = Path.Combine(host.RepoRoot, "notes.md");

		var blame = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "blame", new { path });
		var commits = blame.GetProperty("commits").EnumerateArray().ToList();
		int index = blame.GetProperty("lineCommits").EnumerateArray().ElementAt(3).GetInt32();
		string sha = commits[index].GetProperty("sha").GetString() ?? string.Empty;
		int originalLine = blame.GetProperty("lineOriginals").EnumerateArray().ElementAt(3).GetInt32();
		Assert.Equal("second", commits[index].GetProperty("summary").GetString());
		Assert.Equal(2, originalLine);

		var history = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession, "git", "history", new { path, sha, line = originalLine });

		Assert.Equal(JsonValueKind.Null, history.GetProperty("error").ValueKind);
		Assert.Equal(["second", "first"], Summaries(history));
	}

	[Fact]
	public async Task ALineHistoryWithoutACommitAnchorIsRefused() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\n");
			Commit(repo, "first");
		});

		var history = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession,
			"git",
			"history",
			new { path = Path.Combine(host.RepoRoot, "notes.md"), sha = "HEAD", line = 1 });

		Assert.Contains("isn't a commit", history.GetProperty("error").GetString());
		Assert.Empty(history.GetProperty("commits").EnumerateArray());
	}

	[Fact]
	public async Task ANonPositiveLineIsRefusedRatherThanClampedToTheFirstHunk() {
		string sha = string.Empty;
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.md"), "alpha\nbravo\n");
			Commit(repo, "first");
			sha = ReadHead(repo);
		});

		var hunk = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession,
			"git",
			"commitHunk",
			new { path = Path.Combine(host.RepoRoot, "notes.md"), sha, line = 0 });

		Assert.Equal(JsonValueKind.Null, hunk.GetProperty("hunk").ValueKind);
		Assert.Contains("isn't a line", hunk.GetProperty("error").GetString());
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
