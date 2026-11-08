using System.Text;
using System.Text.Json;
using Weavie.Core.Review;
using Xunit;
using Xunit.Abstractions;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Work budgets for the git-backed operations a user waits on: opening a review, changing its base, the status
/// poll, a page connecting, and the worktree changing under a session. Each budget is an upper bound on the git
/// processes started and the page messages posted, so a per-file or per-session spawn regression fails on any
/// machine instead of surfacing as "it feels slow" on a large repository.
/// The test output lists every process and message counted, so a failure names what started doing more work.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class GitOperationBudgetTests(ITestOutputHelper output) {
	private const int CommittedFiles = 200;
	private const int UncommittedFiles = 20;

	// PERFORMANCE CONTRACT: raising any budget below noticeably requires the user's explicit approval — never raise one
	// just to make a failure pass. Measured 2026-11-08; lower a budget whenever a change makes the operation cheaper.
	private const int OpenReviewGit = 17;
	private const int ReopenReviewGit = 10;
	private const int ChangeBaseGit = 26;
	private const int PollGit = 13;
	private const int ConnectGit = 4;
	private const int FileEditGit = 0;
	private const int CommitGit = 10;
	// The review's file list, history, and commits, plus one hash-only diff message per file.
	private const int ReviewMessages = CommittedFiles + 4;
	private const int PollMessages = 1;
	private const int ConnectMessages = 21;
	private const int FileEditMessages = 1;
	private const int CommitMessages = 2;

	[Fact]
	public async Task OpeningAReview() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);

		await budget.WithinAsync("open review vs origin/main", () => DiffAgainstAsync(host, "origin/main", null),
			OpenReviewGit, ReviewMessages);
	}

	[Fact]
	public async Task ChangingTheReviewBase() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);
		await DiffAgainstAsync(host, "origin/main", null);

		await budget.WithinAsync("change base origin/main -> HEAD", () => DiffAgainstAsync(host, "HEAD", "vs origin/main"),
			ChangeBaseGit, ReviewMessages);
		await budget.WithinAsync("change base HEAD -> origin/main", () => DiffAgainstAsync(host, "origin/main", "vs HEAD"),
			ChangeBaseGit, ReviewMessages);
	}

	[Fact]
	public async Task ReopeningTheSameReview() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);
		await DiffAgainstAsync(host, "origin/main", null);

		await budget.WithinAsync("reopen review vs origin/main", () => DiffAgainstAsync(host, "origin/main", null),
			ReopenReviewGit, ReviewMessages);
	}

	[Fact]
	public async Task OnePullRequestStatusPoll() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);

		await budget.WithinAsync("pull request status poll", () => {
			host.SelectedSession.PullRequestStatus.RequestRefresh();
			return Task.CompletedTask;
		}, lastProcess: "log --format", PollGit, PollMessages);
	}

	[Fact]
	public async Task APageConnecting() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);

		await budget.WithinAsync("page connect", host.ConnectAsync, ConnectGit, ConnectMessages);
	}

	[Fact]
	public async Task EditingAFileOnDisk() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);

		await budget.WithinAsync("external file edit", () => {
			File.AppendAllText(Path.Combine(host.RepoRoot, FileName(0)), "edited\n");
			return Task.CompletedTask;
		}, FileEditGit, FileEditMessages);
	}

	[Fact]
	public async Task CommittingInTheWorktree() {
		await using var host = await StartAsync();
		using var budget = new OperationBudget(host, output);

		await budget.WithinAsync("commit in worktree", () => {
			TempGitRepo.Run(host.RepoRoot, "commit", "--quiet", "-am", "worktree commit");
			return Task.CompletedTask;
		}, lastProcess: "log --format", CommitGit, CommitMessages);
	}

	private static Task<JsonElement> DiffAgainstAsync(TestHost host, string reference, string? replaces) =>
		host.SessionRequestAsync<JsonElement>(host.SelectedSession, "review", "diffAgainst", new { reference, replaces });

	// origin/main holds every file's first version, HEAD changes all of them, and the worktree changes a few more:
	// a branch review spans every file, a HEAD review only the uncommitted ones.
	private static Task<TestHost> StartAsync() => TestHost.StartAsync(
		repo => {
			TempGitRepo.Run(repo, "remote", "add", "origin", "https://github.com/Kapps/weavie.git");
			for (int i = 0; i < CommittedFiles; i++) Write(repo, i, "original");
			TempGitRepo.Run(repo, "add", "-A");
			TempGitRepo.Run(repo, "commit", "--quiet", "-m", "base");
			TempGitRepo.Run(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
			TempGitRepo.Run(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
			for (int i = 0; i < CommittedFiles; i++) Write(repo, i, "branch");
			TempGitRepo.Run(repo, "commit", "--quiet", "-am", "branch");
			for (int i = 0; i < UncommittedFiles; i++) File.AppendAllText(Path.Combine(repo, FileName(i)), "uncommitted\n");
		},
		[new PullRequestSummary {
			Number = 7,
			Title = "Budget branch",
			Author = "Kapps",
			HeadRef = "main",
			HeadSha = string.Empty,
			BaseRef = "main",
			BaseSha = string.Empty,
			Url = "https://github.com/Kapps/weavie/pull/7",
			IsDraft = false,
			State = PullRequestState.Open,
		}]);

	private static string FileName(int index) => $"src/area{index % 10}/file{index}.cs";

	private static void Write(string repo, int index, string version) {
		string path = Path.Combine(repo, FileName(index));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var text = new StringBuilder();
		for (int line = 0; line < 120; line++) text.Append(line % 7 == 0 ? $"// {version} line {line}\n" : $"int value{line} = {line};\n");
		File.WriteAllText(path, text.ToString());
	}
}
