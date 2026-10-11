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
			[(42, new ReviewThread("t1", "feature.txt", 1, "right", false, [new ReviewComment {
				Id = "c1", Author = "ann", AuthorAvatarUrl = "https://a/ann", Url = string.Empty, Body = "why?",
				CreatedAt = "2026-01-01T00:00:00Z", UpdatedAt = "2026-01-01T00:00:00Z", Pending = false, Mine = false,
			}]))],
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
		Assert.Equal("https://a/ann", thread.GetProperty("comments")[0].GetProperty("authorAvatarUrl").GetString());
		Assert.False(loaded.GetProperty("viewerIsAuthor").GetBoolean());
		Assert.Equal(JsonValueKind.Null, loaded.GetProperty("pending").ValueKind);

		string headSha = loaded.GetProperty("headSha").GetString()!;
		var queued = await RequestAsync(host, "comment", new { number = 42, headSha, path = feature, line = 1, body = "because", mode = "review" });

		Assert.True(queued.GetProperty("ok").GetBoolean(), queued.ToString());
		var drafted = await CommentsAsync(host, set => set.GetProperty("threads").GetArrayLength() == 2);
		var draft = drafted.GetProperty("threads")[1].GetProperty("comments")[0];
		Assert.Equal("because", draft.GetProperty("body").GetString());
		Assert.True(draft.GetProperty("mine").GetBoolean());
		Assert.True(draft.GetProperty("pending").GetBoolean());
		Assert.Equal(JsonValueKind.Object, drafted.GetProperty("pending").ValueKind);

		var submitted = await RequestAsync(host, "submitReview", new { number = 42, headSha, verdict = "requestChanges", body = "fix it" });

		Assert.True(submitted.GetProperty("ok").GetBoolean(), submitted.ToString());
		await CommentsAsync(host, set => set.GetProperty("pending").ValueKind == JsonValueKind.Null
			&& !set.GetProperty("threads")[1].GetProperty("comments")[0].GetProperty("pending").GetBoolean());
		Assert.Equal([(42, ReviewVerdict.RequestChanges, "fix it")], provider.SubmittedReviews);
	}

	private static Task<JsonElement> RequestAsync(TestHost host, string type, object payload) =>
		host.SessionRequestAsync<JsonElement>(host.WorkspaceSession, "pullRequests", type, payload);

	private static Task<JsonElement> CommentsAsync(TestHost host, Func<JsonElement, bool> ready) =>
		Wait.ForAsync(() => host.Bridge.LastEvent(host.WorkspaceSession.Address, "pullRequests", "comments") is { } message
			&& message.GetProperty("set") is { ValueKind: JsonValueKind.Object } set
			&& ready(set)
				? set
				: (JsonElement?)null);
}
