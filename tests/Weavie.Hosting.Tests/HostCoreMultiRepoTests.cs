using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Review;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>One workspace holding sessions from two repositories. See docs/specs/multi-repo-workspaces.md.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreMultiRepoTests {
	// The second repository sits beside the home repo, so their labels are "repo" and "infra".
	private static string InfraOf(string home) => Path.Combine(Path.GetDirectoryName(home)!, "infra");

	private static Task<TestHost> StartWithInfraAsync(Action<string> prepareInfra) =>
		TestHost.StartAsync(home => PrepareInfra(home, prepareInfra));

	private static void PrepareInfra(string home, Action<string> prepareInfra) {
		string infra = Directory.CreateDirectory(InfraOf(home)).FullName;
		TempGitRepo.InitCommitted(infra);
		prepareInfra(infra);
	}

	private static void WriteOverlay(string repo, string toml) {
		string overlay = WeaviePaths.WorkspaceSettingsFile(WorkspaceId.ForPath(repo));
		Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
		File.WriteAllText(overlay, toml);
	}

	private static Task<CommandResult> CreateInInfraAsync(TestHost host, string branch) =>
		host.CreateSessionAsync(new NewSessionRequest { Branch = branch, Repo = InfraOf(host.RepoRoot), Base = "main" });

	private static JsonElement InfraEntry(TestHost host, string label) => host.SessionCatalog.Single(entry =>
		entry.GetProperty("label").GetString() == label && entry.GetProperty("repoLabel").GetString() == "infra");

	private static bool Toasted(TestHost host, string text) => host.Bridge.PostedEvents("notifications", "show")
		.Any(toast => toast.GetProperty("message").GetString()!.Contains(text, StringComparison.Ordinal));

	[Fact]
	public async Task TheSameBranchInTwoReposIsTwoSessions() {
		await using var host = await StartWithInfraAsync(_ => { });

		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.True((await CreateInInfraAsync(host, "feature")).Ok);

		var features = host.SessionCatalog.Where(entry => entry.GetProperty("label").GetString() == "feature").ToList();
		Assert.Equal(2, features.Count);
		Assert.Equal(2, features.Select(entry => entry.GetProperty("id").GetString()).Distinct().Count());
		Assert.Equal(
			[InfraOf(host.RepoRoot), host.RepoRoot],
			features.Select(entry => entry.GetProperty("repo").GetString()).Order());
		Assert.All(features, entry => Assert.True(entry.GetProperty("loaded").GetBoolean()));
		Assert.StartsWith(
			WeaviePaths.WorkspaceWorktreesDir(WorkspaceId.ForPath(InfraOf(host.RepoRoot))),
			InfraEntry(host, "feature").GetProperty("worktree").GetString());
	}

	[Fact]
	public async Task ASessionUsesItsOwnReposSetupCommandAndTestProfile() {
		const string profile = """[{"glob":"**/*.tf","symbol":"x","runOne":"terraform test"}]""";
		await using var host = await StartWithInfraAsync(infra => WriteOverlay(
			infra, $"worktree.setupCommand = 'echo infra-setup'\ntest.profile = '{profile}'\n"));

		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);

		await Wait.UntilAsync(() => Toasted(host, "(echo infra-setup)"));
		var pushed = host.Bridge.PostedEvents("tests", "profile")
			.Last(push => push.GetProperty("repo").GetString() == InfraOf(host.RepoRoot));
		Assert.Equal(profile, pushed.GetProperty("profile").GetString());
		// The home repo has no profile at all, so reaching the rules proves the session read its own repo's.
		var entry = InfraEntry(host, "deploy");
		var run = await host.InvokeCommandAsync(
			entry.GetProperty("id").GetString()!,
			CoreCommands.RunTests,
			new { file = Path.Combine(entry.GetProperty("worktree").GetString()!, "main.py") },
			CancellationToken.None);
		Assert.Contains("No test rule in the profile matches", run.Error);
	}

	[Fact]
	public async Task TheFirstSessionInARepoRunsItsAutoConfiguredSetupCommand() {
		await using var host = await StartWithInfraAsync(infra => {
			File.WriteAllText(Path.Combine(infra, "go.mod"), "module weavie/infra\n");
			TempGitRepo.Run(infra, "add", "-A");
			TempGitRepo.Run(infra, "commit", "--quiet", "-m", "go");
		});

		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);

		// Setup waits for the repo's auto-config, which only starts when the repo joins the workspace.
		await Wait.UntilAsync(() => Toasted(host, "Setting up worktree 'deploy'"));
		Assert.True(Toasted(host, "for 'infra'"));
	}

	[Fact]
	public async Task PullRequestStatusUsesTheSessionsRepo() {
		var pullRequest = new PullRequestSummary {
			Number = 7,
			Title = "Deploy",
			Author = "Kapps",
			HeadRef = "deploy",
			HeadSha = string.Empty,
			BaseRef = "main",
			BaseSha = string.Empty,
			Url = "ignored",
			IsDraft = false,
			State = PullRequestState.Open,
		};
		await using var host = await TestHost.StartAsync(
			home => {
				TempGitRepo.Run(home, "remote", "add", "origin", "git@github.com:acme/mono.git");
				PrepareInfra(home, infra => {
					TempGitRepo.Run(infra, "remote", "add", "origin", "git@github.com:acme/infra.git");
					TempGitRepo.Run(infra, "branch", "deploy");
				});
			},
			[pullRequest]);

		// An existing branch, so nothing is fetched from the unreachable origin.
		Assert.True((await host.CreateSessionAsync(
			new NewSessionRequest { Branch = "deploy", Repo = InfraOf(host.RepoRoot), Existing = true })).Ok);

		var session = host.Session(InfraEntry(host, "deploy").GetProperty("id").GetString()!);
		var status = await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "git", "pullRequest") is { } push
			&& push.GetProperty("pullRequest").ValueKind == JsonValueKind.Object ? push : (JsonElement?)null);
		Assert.Equal("https://github.com/acme/infra/pull/7", status.GetProperty("pullRequest").GetProperty("url").GetString());
	}

	[Fact]
	public async Task ARestartRestoresASessionInAnotherRepoAndNoneOfItsOtherWorktrees() {
		await using var host = await StartWithInfraAsync(infra =>
			TempGitRepo.Run(infra, "worktree", "add", "--quiet", "-b", "elsewhere", Path.Combine(Path.GetDirectoryName(infra)!, "elsewhere")));
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		string id = InfraEntry(host, "deploy").GetProperty("id").GetString()!;

		await host.RestartAsync();

		Assert.Equal(id, InfraEntry(host, "deploy").GetProperty("id").GetString());
		Assert.True(InfraEntry(host, "deploy").GetProperty("loaded").GetBoolean());
		Assert.DoesNotContain("elsewhere", host.SessionLabels);
	}

	[Fact]
	public async Task DeletingASessionInAnotherRepoRemovesThatReposWorktree() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		var entry = InfraEntry(host, "deploy");
		string worktree = entry.GetProperty("worktree").GetString()!;
		host.SelectWorkspaceSession();

		Assert.True((await host.DeleteSessionAsync(entry.GetProperty("id").GetString()!, force: false, classify: false)).Ok);

		await Wait.UntilAsync(() => !host.SessionLabels.Contains("deploy"));
		Assert.False(Directory.Exists(worktree));
		Assert.DoesNotContain(worktree, TempGitRepo.Run(InfraOf(host.RepoRoot), "worktree", "list"));
		// Its branch is kept, and the home repo never had one by that name.
		Assert.Contains("deploy", TempGitRepo.Run(InfraOf(host.RepoRoot), "branch", "--list", "deploy"));
		Assert.Empty(TempGitRepo.Run(host.RepoRoot, "branch", "--list", "deploy").Trim());
	}

	[Fact]
	public async Task APlainHomeFolderHostsASessionInARepoBeneathIt() {
		await using var host = await TestHost.StartAsync(home => {
			Directory.Delete(Path.Combine(home, ".git"), recursive: true);
			TempGitRepo.InitCommitted(Directory.CreateDirectory(Path.Combine(home, "infra")).FullName);
		});
		string infra = Path.Combine(host.RepoRoot, "infra");

		var refused = await host.CreateSessionAsync("feature");
		Assert.Contains("isn't a git repository", refused.Error);

		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "deploy", Repo = infra, Base = "main" })).Ok);
		Assert.Equal(infra, host.CatalogEntry("deploy").GetProperty("repo").GetString());
		Assert.True(host.CatalogEntry("deploy").GetProperty("loaded").GetBoolean());
	}

	[Fact]
	public async Task AStartNamesItsRepoForBranchesAndRefusesASourceBaseAcrossRepos() {
		await using var host = await StartWithInfraAsync(infra => TempGitRepo.Run(infra, "branch", "infra-only"));

		string[] home = await host.HostRequestAsync<string[]>("git", "branches", new { sourceId = (string?)null, repo = (string?)null });
		string[] infra = await host.HostRequestAsync<string[]>(
			"git", "branches", new { sourceId = (string?)null, repo = InfraOf(host.RepoRoot) });
		Assert.DoesNotContain("infra-only", home);
		Assert.Contains("infra-only", infra);

		var crossed = await host.CreateSessionAsync(
			new NewSessionRequest { Branch = "deploy", Repo = InfraOf(host.RepoRoot), Base = "source" });
		Assert.Contains("The source session is in 'repo', not 'infra'", crossed.Error);
		var missing = await host.CreateSessionAsync(
			new NewSessionRequest { Branch = "deploy", Repo = Path.Combine(host.RepoRoot, "nowhere"), Base = "main" });
		Assert.Contains("Couldn't open the repository", missing.Error);
	}

	[Fact]
	public async Task PeersReportEachSessionsRepoAndWorktree() {
		await using var host = await StartWithInfraAsync(_ => { });
		Assert.True((await CreateInInfraAsync(host, "deploy")).Ok);
		var entry = InfraEntry(host, "deploy");

		var peers = await host.WorkspaceSession.Peers.ListAsync(includeSides: false, CancellationToken.None);

		var peer = Assert.Single(peers, peer => peer.Id == entry.GetProperty("id").GetString());
		Assert.Equal((InfraOf(host.RepoRoot), entry.GetProperty("worktree").GetString()), (peer.Repo, peer.Worktree));
		Assert.Equal(host.RepoRoot, Assert.Single(peers, peer => peer.Id == host.WorkspaceSession.SlotId).Repo);
	}
}
