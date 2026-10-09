using System.Text.Json;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCorePullRequestCommentsTests {
	private const string Origin = "git@github.com:Kapps/weavie.git";

	[Fact]
	public async Task ABranchWithAPullRequest_LoadsItsCommentsWithoutOpeningIt() {
		// Populated by prepareRepo, which runs before the host (and its PR lookup) starts.
		var pullRequests = new List<PullRequestSummary>();
		var provider = new StaticPullRequestProvider(
			pullRequests,
			[(42, new ReviewComment {
				Id = 1, Path = "feature.txt", Line = 1, Outdated = false, Side = "right", Author = "ann", AuthorAvatarUrl = "https://a/ann", Url = string.Empty, Body = "why?",
				CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z", InReplyTo = 0,
			})],
			new ForgeUser("kapps", "https://a/kapps"));
		await using var host = await TestHost.StartAsync(
			repo => {
				string remote = Path.Combine(Path.GetDirectoryName(repo)!, "remote");
				TempGitRepo.Run(Directory.CreateDirectory(remote).FullName, "init", "--quiet", "--bare");
				TempGitRepo.Run(repo, "branch", "base");
				File.WriteAllText(Path.Combine(repo, "feature.txt"), "feature\n");
				TempGitRepo.Run(repo, "add", "-A");
				TempGitRepo.Run(repo, "commit", "--quiet", "-m", "feature");
				TempGitRepo.Run(repo, "remote", "add", "origin", Origin);
				TempGitRepo.Run(repo, "config", $"url.{remote}.insteadOf", Origin);
				TempGitRepo.Run(repo, "push", "--quiet", "origin", "main", "base");
				pullRequests.Add(new PullRequestSummary {
					Number = 42,
					Title = "Feature",
					Author = "ann",
					HeadRef = "main",
					BaseRef = "base",
					BaseSha = TempGitRepo.Run(repo, "rev-parse", "base").Trim(),
					Url = "u",
					HeadSha = TempGitRepo.Run(repo, "rev-parse", "HEAD").Trim(),
					IsDraft = false,
					State = PullRequestState.Open,
				});
			},
			provider);
		string feature = Path.Combine(host.WorkspaceSession.WorkspaceRoot, "feature.txt");

		var loaded = await CommentsAsync(host, set => set.GetProperty("threads").GetArrayLength() == 1);

		Assert.Equal(42, loaded.GetProperty("number").GetInt32());
		Assert.Equal("kapps", loaded.GetProperty("viewer").GetProperty("login").GetString());
		Assert.Equal("https://a/kapps", loaded.GetProperty("viewer").GetProperty("avatarUrl").GetString());
		Assert.Equal([feature], loaded.GetProperty("changedPaths").EnumerateArray().Select(p => p.GetString()));
		var thread = loaded.GetProperty("threads")[0];
		Assert.Equal(feature, thread.GetProperty("path").GetString());
		Assert.False(thread.GetProperty("comments")[0].GetProperty("mine").GetBoolean());
		Assert.Equal("https://a/ann", thread.GetProperty("comments")[0].GetProperty("avatarUrl").GetString());

		var posted = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession,
			"pullRequests",
			"comment",
			new { number = 42, headSha = loaded.GetProperty("headSha").GetString(), path = feature, line = 1, body = "because" });

		Assert.True(posted.GetProperty("ok").GetBoolean(), posted.ToString());
		var updated = await CommentsAsync(host, set => set.GetProperty("threads").GetArrayLength() == 2);
		var mine = updated.GetProperty("threads")[1].GetProperty("comments")[0];
		Assert.Equal("because", mine.GetProperty("body").GetString());
		Assert.True(mine.GetProperty("mine").GetBoolean());
	}

	private static Task<JsonElement> CommentsAsync(TestHost host, Func<JsonElement, bool> ready) =>
		Wait.ForAsync(() => host.Bridge.LastEvent(host.WorkspaceSession.Address, "pullRequests", "comments") is { } message
			&& message.GetProperty("set") is { ValueKind: JsonValueKind.Object } set
			&& ready(set)
				? set
				: (JsonElement?)null);
}
