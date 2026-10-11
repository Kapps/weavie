using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class PullRequestOverviewTests {
	private static readonly RepoRef Repo = new("github.com", "Kapps", "weavie");
	private static readonly ForgeUser Viewer = new("me", string.Empty);
	private readonly List<PullRequestOverviewSnapshot> _published = [];

	private static PullRequestTarget Target(int number, string head) =>
		new(Repo, "origin", number, head, "base", $"https://github.com/Kapps/weavie/pull/{number}");

	[Fact]
	public async Task Track_LoadsOnceForTheSameTargetAndAgainWhenTheHeadMoves() {
		var discussion = new CountingDiscussion(Provider());
		var overview = new PullRequestOverview(discussion, _published.Add);

		await overview.TrackAsync(Target(7, "a"), CancellationToken.None);
		await overview.TrackAsync(Target(7, "a"), CancellationToken.None);
		await overview.TrackAsync(Target(7, "b"), CancellationToken.None);

		Assert.Equal(2, discussion.Loads);
		Assert.Equal("Front door", Assert.Single(_published).Detail!.Title);
	}

	[Fact]
	public async Task Comment_PostsToThisSessionsPullRequestAndRepublishes() {
		var overview = new PullRequestOverview(Provider(), _published.Add);
		await overview.TrackAsync(Target(7, "a"), CancellationToken.None);

		Assert.False((await overview.CommentAsync(8, "hi", CancellationToken.None)).Ok);
		Assert.False((await overview.CommentAsync(7, "  ", CancellationToken.None)).Ok);
		Assert.True((await overview.CommentAsync(7, "Looks good.", CancellationToken.None)).Ok);

		var item = Assert.Single(overview.Latest.Detail!.Conversation);
		Assert.Equal((Viewer, "Looks good.", (ReviewerState?)null), (item.Author, item.Body, item.Review));
	}

	[Fact]
	public async Task FailedRefresh_KeepsTheLastGoodOverviewWithTheError() {
		var discussion = new CountingDiscussion(Provider());
		var overview = new PullRequestOverview(discussion, _published.Add);
		await overview.TrackAsync(Target(7, "a"), CancellationToken.None);

		discussion.Failure = new HttpRequestException("rate limited");
		await overview.RefreshAsync(CancellationToken.None);

		Assert.Equal("Front door", overview.Latest.Detail!.Title);
		Assert.Equal("Couldn't load PR #7: rate limited", overview.Latest.Error);
	}

	[Fact]
	public async Task NoPullRequest_PublishesAnEmptyOverview() {
		var overview = new PullRequestOverview(Provider(), _published.Add);

		await overview.TrackAsync(null, CancellationToken.None);

		Assert.Equal(new PullRequestOverviewSnapshot(null, null), Assert.Single(_published));
	}

	private static StaticPullRequestProvider Provider() => new(
		[new PullRequestSummary {
			Number = 7, Title = "Front door", Author = "astra", HeadRef = "feature", HeadSha = "a", BaseRef = "main",
			BaseSha = "base", Url = "https://github.com/Kapps/weavie/pull/7", IsDraft = false, State = PullRequestState.Open,
		}],
		[],
		Viewer);

	private sealed class CountingDiscussion(IPullRequestDiscussion inner) : IPullRequestDiscussion {
		public int Loads { get; private set; }

		public Exception? Failure { get; set; }

		public Task<PullRequestDetail> LoadDetailAsync(RepoRef repo, int number, CancellationToken ct = default) {
			Loads++;
			return Failure is { } failure ? Task.FromException<PullRequestDetail>(failure) : inner.LoadDetailAsync(repo, number, ct);
		}

		public Task AddCommentAsync(RepoRef repo, string pullRequestId, string body, CancellationToken ct = default) =>
			inner.AddCommentAsync(repo, pullRequestId, body, ct);
	}
}
