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
	public async Task ASessionGitCannotPlaceAtStartupIsKeptForALaterStart() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		string infra = InfraOf(host.RepoRoot);
		string away = infra + "-away";

		// The repository is unreachable while its worktree is still on disk.
		await host.RestartAsync(() => Directory.Move(infra, away));
		Assert.DoesNotContain("deploy", host.SessionLabels);
		Assert.True(Toasted(host, "The session 'deploy' wasn't restored"));
		// Its workflow documents would otherwise be cleaned up as unowned.
		Assert.True(Toasted(host, "automatic cleanup is stopped"));

		await host.RestartAsync(() => Directory.Move(away, infra));
		Assert.True(InfraEntry(host, "deploy").GetProperty("loaded").GetBoolean());
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

		await Wait.UntilAsync(() => Toasted(host, $"Session 'deploy' was closed: its worktree {worktree} no longer exists."));
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
	public async Task ReposSharingAFolderNameAreLabelledByTheirParentToo() {
		await using var host = await TestHost.StartAsync(home =>
			TempGitRepo.InitCommitted(Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(home)!, "nested", "repo")).FullName));
		string nested = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "nested", "repo");

		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "deploy", Repo = nested, Base = "main" })).Ok);

		Assert.Equal("nested/repo", host.CatalogEntry("deploy").GetProperty("repoLabel").GetString());
		Assert.Equal(
			$"{Path.GetFileName(Path.GetDirectoryName(host.RepoRoot))}/repo",
			host.SessionCatalog.First(entry => entry.GetProperty("workspaceCheckout").GetBoolean()).GetProperty("repoLabel").GetString());
	}
}
