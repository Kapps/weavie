using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class PullRequestCommentsTests : IDisposable {
	private const int Number = 7;
	private readonly TempGitRepo _author = new("weavie-pr-author");
	private readonly TempDirectory _temp = new("weavie-pr-comments");
	private readonly string _worktree;
	private readonly string _headSha;
	private readonly List<PullRequestCommentsSnapshot> _published = [];

	public PullRequestCommentsTests() {
		string bare = _temp.CreateDirectory("remote");
		TempGitRepo.Run(bare, "init", "--quiet", "--bare");
		_author.Write("a.txt", "one\n");
		_author.Write("b.txt", "unchanged\n");
		_author.Commit("base");
		_author.Git("checkout", "--quiet", "-b", "feature");
		_author.Write("a.txt", "one\ntwo\n");
		_author.Write("c.txt", "new\n");
		_headSha = _author.Commit("feature");
		_author.Git("push", "--quiet", bare, "main", $"feature:refs/pull/{Number}/head");
		// The session's clone has only main, so the PR head must be fetched from the forge's pull ref.
		_worktree = _temp.Combine("work");
		TempGitRepo.Run(_temp.Path, "clone", "--quiet", bare, _worktree);
	}

	private PullRequestTarget Target => new(
		new RepoRef("github.com", "Kapps", "weavie"), "origin", Number, _headSha, "main", "https://github.com/Kapps/weavie/pull/7");

	[Fact]
	public async Task NoPullRequest_PublishesAnEmptySet() {
		var comments = Create(Store());

		await comments.TrackAsync(null, CancellationToken.None);

		Assert.Equal(new PullRequestCommentsSnapshot(null, null), Assert.Single(_published));
	}

	[Fact]
	public async Task Refresh_FetchesTheHeadAndLoadsOnlyThisPrsThreads() {
		var comments = Create(Store());

		await comments.TrackAsync(Target, CancellationToken.None);

		var set = Assert.IsType<PullRequestCommentSet>(comments.Latest.Set);
		Assert.Null(comments.Latest.Error);
		Assert.Equal(["a.txt", "c.txt"], set.ChangedPaths);
		Assert.Equal("me", set.Viewer.Login);
		var thread = Assert.Single(set.Threads);
		Assert.Equal([1L, 2L], thread.Comments.Select(c => c.Id));
		Assert.Equal([false, true], thread.Comments.Select(set.IsMine));
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

		var result = await comments.CommentAsync(Number, _headSha, "b.txt", 1, "hi", CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("isn't changed", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Comment_RejectsAStaleHead() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		var result = await comments.CommentAsync(Number, new string('b', 40), "a.txt", 2, "hi", CancellationToken.None);

		Assert.Equal("PR #7 has new commits — comments reloaded; try again.", result.Error);
	}

	[Fact]
	public async Task Edit_OnlyTheViewersOwnComments() {
		var comments = Create(Store());
		await comments.TrackAsync(Target, CancellationToken.None);

		var foreign = await comments.EditAsync(Number, 1, "rewritten", CancellationToken.None);
		var own = await comments.EditAsync(Number, 2, "rewritten", CancellationToken.None);

		Assert.Equal("You can only edit your own comments.", foreign.Error);
		Assert.True(own.Ok);
		Assert.Equal("rewritten", comments.Latest.Set?.Threads[0].Comments[1].Body);
	}

	[Fact]
	public async Task Post_AConcurrentRefreshNeverPublishesASetMissingThePost() {
		var store = new GatedStore(Store());
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);

		var post = comments.CommentAsync(Number, _headSha, "c.txt", 1, "posted", CancellationToken.None);
		await store.AddStarted.Task;
		var refresh = comments.RefreshAsync(CancellationToken.None);
		int before = _published.Count;
		store.ReleaseAdd.SetResult();
		Assert.True((await post).Ok);
		await refresh;

		// The post publishes; the refresh queued behind it finds nothing newer and stays quiet.
		Assert.Equal(before + 1, _published.Count);
		Assert.All(_published.Skip(before), snapshot =>
			Assert.Contains(snapshot.Set!.Threads, t => t.Comments[0].Body == "posted"));
	}

	[Fact]
	public async Task Track_TheSameTargetDoesNotReload() {
		var store = new GatedStore(Store());
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);
		store.FailList = true;

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

		await store.ReplyAsync(Target.Repo, Number, 1, "from someone else", CancellationToken.None);
		await comments.RefreshAsync(CancellationToken.None);
		Assert.Equal(2, _published.Count);
		Assert.Equal("from someone else", comments.Latest.Set?.Threads[0].Comments[^1].Body);
	}

	[Fact]
	public async Task ForgeFailure_KeepsTheLastGoodSetAndReportsTheError() {
		var store = new GatedStore(Store());
		var comments = Create(store);
		await comments.TrackAsync(Target, CancellationToken.None);
		store.FailList = true;

		await comments.RefreshAsync(CancellationToken.None);

		Assert.NotNull(comments.Latest.Set);
		Assert.Contains("GitHub is down", comments.Latest.Error, StringComparison.Ordinal);
	}

	public void Dispose() {
		_author.Dispose();
		_temp.Dispose();
	}

	private PullRequestComments Create(IReviewCommentStore store) => new(_worktree, store, _published.Add);

	private static StaticPullRequestProvider Store() => new(
		[],
		[
			(Number, Comment(1, 0, "ann")),
			(Number, Comment(2, 1, "Me")),
			(8, Comment(3, 0, "ann")),
		],
		new ForgeUser("me", string.Empty));

	private static ReviewComment Comment(long id, long inReplyTo, string author) => new() {
		Id = id,
		Path = "a.txt",
		Line = 2,
		Outdated = false,
		Side = "right",
		Author = author,
		AuthorAvatarUrl = string.Empty,
		Url = string.Empty,
		Body = $"comment {id}",
		CreatedAt = $"2026-01-0{id}T00:00:00Z",
		UpdatedAt = $"2026-01-0{id}T00:00:00Z",
		InReplyTo = inReplyTo,
	};

	private sealed class GatedStore(StaticPullRequestProvider inner) : IReviewCommentStore {
		public TaskCompletionSource AddStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseAdd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool FailList { get; set; }

		public Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) =>
			FailList ? throw new HttpRequestException("GitHub is down") : inner.ListAsync(repo, number, ct);

		public async Task<ReviewComment> AddAsync(RepoRef repo, int number, string commitId, NewReviewComment draft, CancellationToken ct = default) {
			AddStarted.SetResult();
			await ReleaseAdd.Task;
			return await inner.AddAsync(repo, number, commitId, draft, ct);
		}

		public Task<ReviewComment> ReplyAsync(RepoRef repo, int number, long inReplyTo, string body, CancellationToken ct = default) =>
			inner.ReplyAsync(repo, number, inReplyTo, body, ct);

		public Task<ReviewComment> EditAsync(RepoRef repo, long id, string body, CancellationToken ct = default) =>
			inner.EditAsync(repo, id, body, ct);

		public Task<ForgeUser> ViewerAsync(RepoRef repo, CancellationToken ct = default) => inner.ViewerAsync(repo, ct);
	}
}
