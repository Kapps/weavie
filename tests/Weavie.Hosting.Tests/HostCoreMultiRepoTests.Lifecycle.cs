using System.Text.Json;
using Weavie.Core.Review;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

// How a repo joins and leaves a workspace, and what happens to its sessions when it can't be reached.
public sealed partial class HostCoreMultiRepoTests {
	[Fact]
	public async Task ARepoJoinsWithItsFirstSessionAndLeavesWithItsLast() {
		await using var host = await StartWithInfraAsync(_ => { });
		string infra = InfraOf(host.RepoRoot);

		// A path relative to the session it is typed in names the repo.
		Assert.True((await host.CreateSessionAsync(
			new NewSessionRequest { Branch = "deploy", Repo = Path.Combine("..", "infra"), Base = "main" })).Ok);
		Assert.Equal([infra, host.RepoRoot], (await HelloReposAsync(host)).Order());

		host.SelectWorkspaceSession();
		Assert.True((await host.DeleteSessionAsync(
			InfraEntry(host, "deploy").GetProperty("id").GetString()!, force: false, classify: false)).Ok);
		await Wait.UntilAsync(() => !host.SessionLabels.Contains("deploy"));
		Assert.Equal([host.RepoRoot], await HelloReposAsync(host));
	}

	[Fact]
	public async Task ARepoTheWorkspaceHasUsedIsStillOfferedAfterItsLastSessionAndARestart() {
		await using var host = await StartWithInfraAsync(_ => { });
		string home = Path.GetFileName(host.RepoRoot);
		Assert.Equal([home], await OfferedReposAsync(host));

		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		Assert.Equal([home, "infra"], host.Bridge.LastEvent("repos", "known")!.Value.EnumerateArray()
			.Select(repo => repo.GetProperty("label").GetString()));

		host.SelectWorkspaceSession();
		Assert.True((await host.DeleteSessionAsync(
			InfraEntry(host, "deploy").GetProperty("id").GetString()!, force: false, classify: false)).Ok);
		await host.RestartAsync();
		Assert.Equal([home, "infra"], await OfferedReposAsync(host));

		// A repo whose folder is gone is no longer offered.
		await host.RestartAsync(() => Directory.Delete(InfraOf(host.RepoRoot), recursive: true));
		Assert.Equal([home], await OfferedReposAsync(host));
	}

	[Fact]
	public async Task ASessionGitCannotPlaceStaysListedUnloadedAndReturnsToItsRepoWhenItIsBack() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		string worktree = InfraEntry(host, "deploy").GetProperty("worktree").GetString()!;
		string infra = InfraOf(host.RepoRoot);
		string away = infra + "-away";

		// The repository is unreachable while its worktree is still on disk.
		await host.RestartAsync(() => Directory.Move(infra, away));
		Assert.Equal(worktree, host.CatalogEntry("deploy").GetProperty("repo").GetString());
		Assert.False(host.CatalogEntry("deploy").GetProperty("loaded").GetBoolean());
		Assert.True(Toasted(host, "The session 'deploy' can't find its repository"));
		Assert.False(Toasted(host, "Couldn't restore the session"));
		// It is still a session, so its workflow documents stay owned.
		Assert.False(Toasted(host, "automatic cleanup is stopped"));

		await host.RestartAsync(() => Directory.Move(away, infra));
		Assert.Equal(infra, InfraEntry(host, "deploy").GetProperty("repo").GetString());
	}

	[Fact]
	public async Task ASessionWhoseRepositoryIsGoneCanBeDeletedAndItsFolderIsKept() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		string worktree = InfraEntry(host, "deploy").GetProperty("worktree").GetString()!;
		await host.RestartAsync(() => Directory.Delete(InfraOf(host.RepoRoot), recursive: true));

		host.SelectWorkspaceSession();
		Assert.True((await host.DeleteSessionAsync(
			host.CatalogEntry("deploy").GetProperty("id").GetString()!, force: false, classify: false)).Ok);
		await Wait.UntilAsync(() => !host.SessionLabels.Contains("deploy"));
		Assert.True(Directory.Exists(worktree));

		host.Bridge.Clear();
		await host.RestartAsync();
		Assert.DoesNotContain("deploy", host.SessionLabels);
		Assert.False(Toasted(host, "can't find its repository"));
	}

	[Fact]
	public async Task AVanishedWorktreeInAnotherRepoClosesItsSessionEvenWhenTheRepoIsGone() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		var session = host.Session(InfraEntry(host, "deploy").GetProperty("id").GetString()!);
		string worktree = session.WorkspaceRoot;

		Directory.Delete(InfraOf(host.RepoRoot), recursive: true);
		Directory.Delete(worktree, recursive: true);
		host.SessionEvent(session, "files", "refreshIndex", new { });

		await Wait.UntilAsync(() => Toasted(host, $"Session 'infra / deploy' was closed: its worktree {worktree} no longer exists."));
		Assert.DoesNotContain("deploy", host.SessionLabels);
		Assert.False(Toasted(host, "Couldn't close session"));
	}

	[Fact]
	public async Task OpeningAPullRequestFromASessionOpensItInThatSessionsRepo() {
		var pullRequest = new PullRequestSummary {
			Number = 9,
			Title = "Rollout",
			Author = "Kapps",
			HeadRef = "rollout",
			HeadSha = string.Empty,
			BaseRef = "main",
			BaseSha = string.Empty,
			Url = "ignored",
			IsDraft = false,
			State = PullRequestState.Open,
		};
		await using var host = await TestHost.StartAsync(
			home => PrepareInfra(home, infra => {
				TempGitRepo.Run(infra, "remote", "add", "origin", "git@github.com:acme/infra.git");
				TempGitRepo.Run(infra, "branch", "deploy");
				TempGitRepo.Run(infra, "branch", "rollout");
			}),
			[pullRequest]);
		Assert.True((await host.CreateSessionAsync(
			new NewSessionRequest { Branch = "deploy", Repo = InfraOf(host.RepoRoot), Existing = true })).Ok);
		var session = host.Session(InfraEntry(host, "deploy").GetProperty("id").GetString()!);

		// Its diff needs the unreachable origin, so the open reports that; the session is what is under test.
		await host.SessionRequestAsync<JsonElement>(session, "pullRequests", "open", new { number = 9, owner = "", repo = "" });

		Assert.True(InfraEntry(host, "rollout").GetProperty("loaded").GetBoolean());
		Assert.Empty(TempGitRepo.Run(host.RepoRoot, "branch", "--list", "rollout").Trim());
	}

	[Fact]
	public async Task OpeningABranchGoesToTheWorktreeGitHasItOnNotToALabel() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		var first = host.Session(InfraEntry(host, "deploy").GetProperty("id").GetString()!);
		// The session's agent moves its worktree to another branch; its label still says "deploy".
		TempGitRepo.Run(first.WorkspaceRoot, "switch", "--quiet", "-c", "rollout");

		Assert.True((await host.CreateSessionAsync(
			new NewSessionRequest { Branch = "deploy", Repo = InfraOf(host.RepoRoot), Existing = true })).Ok);

		var onDeploy = Assert.Single(host.SessionCatalog, entry =>
			entry.GetProperty("repoLabel").GetString() == "infra"
			&& TempGitRepo.Run(entry.GetProperty("worktree").GetString()!, "branch", "--show-current").Trim() == "deploy");
		Assert.NotEqual(first.SlotId, onDeploy.GetProperty("id").GetString());
	}

	[Fact]
	public async Task ReposSharingAFolderNameAreLabelledByTheirParentToo() {
		await using var host = await TestHost.StartAsync(home =>
			TempGitRepo.InitCommitted(Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(home)!, "nested", "repo")).FullName));
		string nested = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "nested", "repo");

		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "deploy", Repo = nested, Base = "main" })).Ok);

		Assert.Equal("nested/repo", host.CatalogEntry("deploy").GetProperty("repoLabel").GetString());
		Assert.Equal(
			$"{Path.GetFileName(Path.GetDirectoryName(host.RepoRoot))}/repo",
			host.CatalogEntry("main").GetProperty("repoLabel").GetString());
	}
}
