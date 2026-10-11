using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class PullRequestCommentsTests : IDisposable {
	private const int Number = 7;
	private readonly TempGitRepo _author = new("weavie-pr-author");
	private readonly TempDirectory _temp = new("weavie-pr-comments");
	private readonly string _worktree;
	private readonly string _baseSha;
	private readonly string _headSha;
	private readonly List<PullRequestCommentsSnapshot> _published = [];

	public PullRequestCommentsTests() {
		string bare = _temp.CreateDirectory("remote");
		TempGitRepo.Run(bare, "init", "--quiet", "--bare");
		_author.Write("a.txt", "one\n");
		_author.Write("b.txt", "unchanged\n");
		_baseSha = _author.Commit("base");
		_author.Git("checkout", "--quiet", "-b", "feature");
		_author.Write("a.txt", "one\ntwo\n");
		_author.Write("c.txt", "new\n");
		_headSha = _author.Commit("feature");
		_author.Git("push", "--quiet", bare, "main", $"feature:refs/pull/{Number}/head");
		// The session's clone has only main, so the PR head must be fetched by sha.
		_worktree = _temp.Combine("work");
		TempGitRepo.Run(_temp.Path, "clone", "--quiet", bare, _worktree);
	}

	private PullRequestTarget Target => new(
		new RepoRef("github.com", "Kapps", "weavie"), "origin", Number, _headSha, _baseSha, "https://github.com/Kapps/weavie/pull/7");

	[Fact]
	public async Task NoPullRequest_PublishesAnEmptySet() {
		var comments = Create(Store());

		await comments.TrackAsync(null, CancellationToken.None);

		Assert.Equal(new PullRequestCommentsSnapshot(null, null), Assert.Single(_published));
	}

	[Fact]
	public async Task Refresh_FetchesTheHeadWithoutMovingARefAndLoadsOnlyThisPrsThreads() {
		var comments = Create(Store());
		string refs = TempGitRepo.Run(_worktree, "for-each-ref");

		await comments.TrackAsync(Target, CancellationToken.None);

		Assert.Equal(refs, TempGitRepo.Run(_worktree, "for-each-ref"));

		var set = Assert.IsType<PullRequestCommentSet>(comments.Latest.Set);
		Assert.Null(comments.Latest.Error);
		Assert.Equal(["a.txt", "c.txt"], set.ChangedPaths);
		Assert.Equal("me", set.Review.Viewer.Login);
		var thread = Assert.Single(set.Review.Threads);
		Assert.Equal(["1", "2"], thread.Comments.Select(c => c.Id));
		Assert.Equal([false, true], thread.Comments.Select(c => c.Mine));
	}

	[Fact]
	public async Task Sources_ReadTheHeadAndTheMergeBase() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		Assert.Equal(new PullRequestSources("one\ntwo\n", "one\n"), await comments.SourcesAsync("a.txt", _headSha, CancellationToken.None));
		Assert.Equal(new PullRequestSources("new\n", null), await comments.SourcesAsync("c.txt", _headSha, CancellationToken.None));
	}

	[Fact]
	public async Task Comment_RejectsAPathThePrDoesNotChange() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		var result = await comments.CommentAsync(Number, _headSha, "b.txt", 1, "hi", CommentMode.Now, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("isn't changed", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Comment_RejectsAStaleHead() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		var result = await comments.CommentAsync(Number, new string('b', 40), "a.txt", 2, "hi", CommentMode.Now, CancellationToken.None);

		Assert.Equal("PR #7 has new commits — comments reloaded; try again.", result.Error);
	}

	[Fact]
	public async Task Comment_NowPostsAndAQueuedOneStartsAPendingReviewTheNextJoins() {
		var store = Store();
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		Assert.True((await comments.CommentAsync(Number, _headSha, "c.txt", 1, "now", CommentMode.Now, CancellationToken.None)).Ok);
		Assert.Null(Review(comments).Pending);

		Assert.True((await comments.CommentAsync(Number, _headSha, "c.txt", 1, "first", CommentMode.Review, CancellationToken.None)).Ok);
		var pending = Assert.IsType<PendingReview>(Review(comments).Pending);
		Assert.True((await comments.CommentAsync(Number, _headSha, "a.txt", 2, "second", CommentMode.Review, CancellationToken.None)).Ok);

		Assert.Equal(pending, Review(comments).Pending);
		Assert.Equal(
			[("now", false), ("first", true), ("second", true)],
			Review(comments).Threads.Skip(1).Select(t => (t.Comments[0].Body, t.Comments[0].Pending)));
	}

	[Fact]
	public async Task Comment_NowIsRefusedWhileAReviewIsPending() {
		var store = Store();
		store.PendingReviews[Number] = new PendingReview("started-on-github", string.Empty);
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		var comment = await comments.CommentAsync(Number, _headSha, "c.txt", 1, "now", CommentMode.Now, CancellationToken.None);
		var reply = await comments.ReplyAsync(Number, "t1", "now", CommentMode.Now, CancellationToken.None);

		Assert.Equal("You have a pending review — new comments join it.", comment.Error);
		Assert.Equal(comment.Error, reply.Error);
		Assert.Single(Review(comments).Threads);
	}

	[Fact]
	public async Task Reply_QueuedStartsAReviewWhenNoneIsPendingAndNowPostsImmediately() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		Assert.True((await comments.ReplyAsync(Number, "t1", "posted", CommentMode.Now, CancellationToken.None)).Ok);
		Assert.True((await comments.ReplyAsync(Number, "t1", "queued", CommentMode.Review, CancellationToken.None)).Ok);

		Assert.NotNull(Review(comments).Pending);
		Assert.Equal(
			[("posted", false), ("queued", true)],
			Review(comments).Threads[0].Comments.Skip(2).Select(c => (c.Body, c.Pending)));
	}

	[Fact]
	public async Task Edit_OnlyTheViewersOwnComments() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		var foreign = await comments.EditAsync(Number, "1", "rewritten", CancellationToken.None);
		var own = await comments.EditAsync(Number, "2", "rewritten", CancellationToken.None);

		Assert.Equal("You can only edit your own comments.", foreign.Error);
		Assert.True(own.Ok);
		Assert.Equal("rewritten", Review(comments).Threads[0].Comments[1].Body);
	}

	[Fact]
	public async Task DiscardComment_OnlyRemovesTheViewersDrafts() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);
		await comments.ReplyAsync(Number, "t1", "draft", CommentMode.Review, CancellationToken.None);
		string draft = Review(comments).Threads[0].Comments[^1].Id;

		var posted = await comments.DiscardCommentAsync(Number, "2", CancellationToken.None);
		var pending = await comments.DiscardCommentAsync(Number, draft, CancellationToken.None);

		Assert.Equal("Only your pending comments can be discarded.", posted.Error);
		Assert.True(pending.Ok);
		Assert.Equal(["1", "2"], Review(comments).Threads[0].Comments.Select(c => c.Id));
	}

	[Fact]
	public async Task Submit_PublishesTheDraftsWithTheVerdictAndSummary() {
		var store = Store();
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);
		await comments.CommentAsync(Number, _headSha, "c.txt", 1, "draft", CommentMode.Review, CancellationToken.None);

		var stale = await comments.SubmitReviewAsync(Number, new string('b', 40), ReviewVerdict.Approve, "lgtm", CancellationToken.None);
		var submitted = await comments.SubmitReviewAsync(Number, _headSha, ReviewVerdict.RequestChanges, "fix it", CancellationToken.None);

		Assert.Equal("PR #7 has new commits — comments reloaded; try again.", stale.Error);
		Assert.True(submitted.Ok, submitted.Error);
		Assert.Null(Review(comments).Pending);
		Assert.False(Review(comments).Threads[^1].Comments[0].Pending);
		Assert.Equal([(Number, ReviewVerdict.RequestChanges, "fix it")], store.SubmittedReviews);
	}

	[Fact]
	public async Task Submit_AVerdictWithNothingPendingPostsAReviewOfItsOwn() {
		var store = Store();
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		var empty = await comments.SubmitReviewAsync(Number, _headSha, ReviewVerdict.Comment, " ", CancellationToken.None);
		var approved = await comments.SubmitReviewAsync(Number, _headSha, ReviewVerdict.Approve, string.Empty, CancellationToken.None);

		Assert.Equal("Write a summary or add a comment to submit a review.", empty.Error);
		Assert.True(approved.Ok, approved.Error);
		Assert.Equal([(Number, ReviewVerdict.Approve, string.Empty)], store.SubmittedReviews);
	}

	[Fact]
	public async Task Submit_OnTheViewersOwnPrOnlyComments() {
		var store = Store("me");
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);
		await comments.CommentAsync(Number, _headSha, "c.txt", 1, "note to self", CommentMode.Review, CancellationToken.None);

		var approve = await comments.SubmitReviewAsync(Number, _headSha, ReviewVerdict.Approve, string.Empty, CancellationToken.None);
		var comment = await comments.SubmitReviewAsync(Number, _headSha, ReviewVerdict.Comment, string.Empty, CancellationToken.None);

		Assert.True(Review(comments).ViewerIsAuthor);
		Assert.Equal("You can only comment on your own pull request.", approve.Error);
		Assert.True(comment.Ok, comment.Error);
	}

	[Fact]
	public async Task DiscardReview_DeletesEveryDraft() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);
		await comments.CommentAsync(Number, _headSha, "c.txt", 1, "draft thread", CommentMode.Review, CancellationToken.None);
		await comments.ReplyAsync(Number, "t1", "draft reply", CommentMode.Review, CancellationToken.None);

		Assert.True((await comments.DiscardReviewAsync(Number, CancellationToken.None)).Ok);

		Assert.Null(Review(comments).Pending);
		Assert.Equal(["1", "2"], Assert.Single(Review(comments).Threads).Comments.Select(c => c.Id));
		Assert.Equal("You have no pending review on PR #7.", (await comments.DiscardReviewAsync(Number, CancellationToken.None)).Error);
	}

	[Fact]
	public async Task FailedWrite_StillReloadsWhatTheForgeChanged() {
		var store = new GatedStore(Store()) { FailReply = true };
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		var result = await comments.ReplyAsync(Number, "t1", "queued", CommentMode.Review, CancellationToken.None);

		Assert.Equal("GitHub is down", result.Error);
		Assert.NotNull(Review(comments).Pending);
	}

	[Fact]
	public async Task Post_AConcurrentRefreshNeverPublishesASetMissingThePost() {
		var store = new GatedStore(Store());
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		var post = comments.CommentAsync(Number, _headSha, "c.txt", 1, "posted", CommentMode.Now, CancellationToken.None);
		await store.CreateStarted.Task;
		var refresh = comments.RefreshAsync(CancellationToken.None);
		int before = _published.Count;
		store.ReleaseCreate.SetResult();
		Assert.True((await post).Ok);
		await refresh;

		// The post publishes; the refresh queued behind it finds nothing newer and stays quiet.
		Assert.Equal(before + 1, _published.Count);
		Assert.All(_published.Skip(before), snapshot =>
			Assert.Contains(snapshot.Set!.Review.Threads, t => t.Comments[0].Body == "posted"));
	}

	[Fact]
	public async Task Track_TheSameTargetDoesNotReload() {
		var store = new GatedStore(Store());
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);
		store.FailLoad = true;

		await comments.TrackAsync(Target, CancellationToken.None);

		Assert.Single(_published);
		Assert.Null(comments.Latest.Error);
	}

	[Fact]
	public async Task Refresh_PublishesOnlyWhenTheCommentsChanged() {
		var store = Store();
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		await comments.RefreshAsync(CancellationToken.None);
		Assert.Single(_published);

		await store.ReplyAsync(Target.Repo, "t1", null, "from someone else", CancellationToken.None);
		await comments.RefreshAsync(CancellationToken.None);
		Assert.Equal(2, _published.Count);
		Assert.Equal("from someone else", Review(comments).Threads[0].Comments[^1].Body);
	}

	[Fact]
	public async Task ForgeFailure_KeepsTheLastGoodSetAndReportsTheError() {
		var store = new GatedStore(Store());
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);
		store.FailLoad = true;

		await comments.RefreshAsync(CancellationToken.None);

		Assert.NotNull(comments.Latest.Set);
		Assert.Contains("GitHub is down", comments.Latest.Error, StringComparison.Ordinal);
	}

	public void Dispose() {
		_author.Dispose();
		_temp.Dispose();
	}

	private static PullRequestReview Review(PullRequestComments comments) => comments.Latest.Set!.Review;

	private PullRequestComments Create(IReviewCommentStore store) => new(_worktree, store, _published.Add);

	private static StaticPullRequestProvider Store() => Store("ann");

	private static StaticPullRequestProvider Store(string author) => new(
		[Pr(Number, author), Pr(8, "ann")],
		[
			(Number, new ReviewThread("t1", "a.txt", 2, "right", false, [Comment("1", "ann"), Comment("2", "Me")])),
			(8, new ReviewThread("t3", "a.txt", 2, "right", false, [Comment("3", "ann")])),
		],
		new ForgeUser("me", string.Empty));

	private static PullRequestSummary Pr(int number, string author) => new() {
		Number = number,
		Title = "Feature",
		Author = author,
		HeadRef = "feature",
		HeadSha = string.Empty,
		BaseRef = "main",
		BaseSha = string.Empty,
		Url = string.Empty,
		IsDraft = false,
		State = PullRequestState.Open,
	};

	private static ReviewComment Comment(string id, string author) => new() {
		Id = id,
		Author = author,
		AuthorAvatarUrl = string.Empty,
		Url = string.Empty,
		Body = $"comment {id}",
		CreatedAt = $"2026-01-0{id}T00:00:00Z",
		UpdatedAt = $"2026-01-0{id}T00:00:00Z",
		Pending = false,
		Mine = false,
	};

	private sealed class GatedStore(StaticPullRequestProvider inner) : IReviewCommentStore {
		public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool FailLoad { get; set; }
		public bool FailReply { get; init; }

		public Task<PullRequestReview> LoadAsync(RepoRef repo, int number, CancellationToken ct = default) =>
			FailLoad ? throw new HttpRequestException("GitHub is down") : inner.LoadAsync(repo, number, ct);

		public async Task<string> CreateReviewAsync(
			RepoRef repo, string pullRequestId, string commitSha, ReviewVerdict? verdict, string body,
			IReadOnlyList<NewReviewComment> threads, CancellationToken ct = default) {
			if (verdict is not null) {
				CreateStarted.SetResult();
				await ReleaseCreate.Task;
			}

			return await inner.CreateReviewAsync(repo, pullRequestId, commitSha, verdict, body, threads, ct);
		}

		public Task AddThreadAsync(RepoRef repo, string reviewId, NewReviewComment draft, CancellationToken ct = default) =>
			inner.AddThreadAsync(repo, reviewId, draft, ct);

		public Task ReplyAsync(RepoRef repo, string threadId, string? reviewId, string body, CancellationToken ct = default) =>
			FailReply ? throw new HttpRequestException("GitHub is down") : inner.ReplyAsync(repo, threadId, reviewId, body, ct);

		public Task EditAsync(RepoRef repo, string commentId, string body, CancellationToken ct = default) =>
			inner.EditAsync(repo, commentId, body, ct);

		public Task DeleteCommentAsync(RepoRef repo, string commentId, CancellationToken ct = default) =>
			inner.DeleteCommentAsync(repo, commentId, ct);

		public Task SubmitReviewAsync(RepoRef repo, string reviewId, ReviewVerdict verdict, string body, CancellationToken ct = default) =>
			inner.SubmitReviewAsync(repo, reviewId, verdict, body, ct);

		public Task DiscardReviewAsync(RepoRef repo, string reviewId, CancellationToken ct = default) =>
			inner.DiscardReviewAsync(repo, reviewId, ct);
	}
}
