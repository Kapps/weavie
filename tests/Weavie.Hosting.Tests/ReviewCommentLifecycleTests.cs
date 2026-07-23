using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Review-comment request fencing across refreshes, review replacement, and session lifetime.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class ReviewCommentLifecycleTests {
	[Fact]
	public async Task CommentRefreshFromAnUnloadedSession_CannotOverwriteTheReloadedReview() {
		var comments = new DelayedReviewComments();
		await using var host = await TestHost.StartWithReviewCommentsAsync(_ => { }, comments);
		Assert.True((await host.CreateSessionAsync("comment-fence")).Ok);
		var session = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		string path = ArmPrReview(session);
		PostComment(host, path, "start the old refresh");
		await Wait.ForAsync(() => comments.ListCalls == 1 ? 1 : (int?)null);
		Assert.True((await host.Core.UnloadSessionAsync("comment-fence", CancellationToken.None)).Ok);
		Assert.True((await host.Core.LoadSessionAsync("comment-fence", CancellationToken.None)).Ok);
		host.Send("""{"type":"switch-session","id":"comment-fence"}""");
		await Wait.ForAsync(() => comments.ListCalls == 2 ? 2 : (int?)null);

		comments.CompleteOldRefresh();
		host.Bridge.Clear();
		host.Send(JsonSerializer.Serialize(new { type = "get-turn-diff", path }));

		var pushed = Assert.IsType<JsonElement>(host.Bridge.LastOfType("review-comments"));
		var comment = Assert.Single(pushed.GetProperty("comments").EnumerateArray());
		Assert.Equal("reloaded session", comment.GetProperty("body").GetString());
	}

	[Fact]
	public async Task OlderCommentRefresh_CannotOverwriteANewerRefreshForTheSameReview() {
		var comments = new DelayedReviewComments();
		await using var host = await TestHost.StartWithReviewCommentsAsync(_ => { }, comments);
		Assert.True((await host.CreateSessionAsync("comment-order")).Ok);
		string path = ArmPrReview(Assert.IsType<HostSession>(host.Core.ActiveSessionForTest()));
		PostComment(host, path, "first refresh");
		await Wait.ForAsync(() => comments.ListCalls == 1 ? 1 : (int?)null);
		PostComment(host, path, "newer refresh");
		await Wait.ForAsync(() => comments.ListCalls == 2 ? 2 : (int?)null);

		comments.CompleteOldRefresh();
		host.Bridge.Clear();
		host.Send(JsonSerializer.Serialize(new { type = "get-turn-diff", path }));

		var pushed = Assert.IsType<JsonElement>(host.Bridge.LastOfType("review-comments"));
		var comment = Assert.Single(pushed.GetProperty("comments").EnumerateArray());
		Assert.Equal("reloaded session", comment.GetProperty("body").GetString());
	}

	[Fact]
	public async Task UnloadingAReview_CancelsAPendingCommentBeforeTheStoreMutates() {
		var comments = new GatedReviewComments();
		await using var host = await TestHost.StartWithReviewCommentsAsync(_ => { }, comments);
		Assert.True((await host.CreateSessionAsync("comment-cancel")).Ok);
		string path = ArmPrReview(Assert.IsType<HostSession>(host.Core.ActiveSessionForTest()));

		PostComment(host, path, "must not land after unload");
		await comments.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True((await host.Core.UnloadSessionAsync("comment-cancel", CancellationToken.None)).Ok);
		await comments.AddCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, comments.CompletedAdds);
	}

	[Fact]
	public async Task CommentForAFileOutsideTheArmedReview_IsRejectedBeforeCallingTheStore() {
		var comments = new GatedReviewComments();
		await using var host = await TestHost.StartWithReviewCommentsAsync(_ => { }, comments);
		Assert.True((await host.CreateSessionAsync("comment-path")).Ok);
		var session = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		ArmPrReview(session);

		PostComment(host, Path.Combine(session.WorkspaceRoot, "not-reviewed.txt"), "invalid target");

		Assert.Equal(0, comments.AddCalls);
	}

	private static string ArmPrReview(HostSession session) {
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		string disk = "changed\n";
		File.WriteAllText(path, disk);
		long token = session.Changes.BeginReviewArm();
		var review = new ReviewIdentity(
			42, "PR #42", "head-ref", "base", "head", new RepoRef("github.com", "weavie", "test"), session.WorkspaceRoot);
		Assert.True(session.Changes.ArmReview(
			token,
			review,
			[new ReviewSeed(path, "hello\n", disk, ExistedAtRef: true, ExistsOnDisk: true)]));
		return path;
	}

	private static void PostComment(TestHost host, string path, string body) => host.Send(JsonSerializer.Serialize(new {
		type = "add-pr-comment",
		number = 42,
		path,
		line = 1,
		side = "right",
		body,
	}));

	private sealed class DelayedReviewComments : IReviewCommentStore {
		private readonly TaskCompletionSource<IReadOnlyList<ReviewComment>> _oldRefresh = new();
		private int _listCalls;

		public int ListCalls => Volatile.Read(ref _listCalls);

		public Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) =>
			Interlocked.Increment(ref _listCalls) == 1
				? _oldRefresh.Task
				: Task.FromResult<IReadOnlyList<ReviewComment>>([Comment("reloaded session")]);

		public Task<ReviewComment> AddAsync(
			RepoRef repo,
			int number,
			string commitId,
			NewReviewComment draft,
			CancellationToken ct = default) => Task.FromResult(Comment(draft.Body));

		public Task<ReviewComment> ReplyAsync(
			RepoRef repo,
			int number,
			long inReplyTo,
			string body,
			CancellationToken ct = default) => Task.FromResult(Comment(body));

		public void CompleteOldRefresh() => _oldRefresh.SetResult([Comment("unloaded session")]);

		private static ReviewComment Comment(string body) => new() {
			Id = 1,
			Path = "readme.txt",
			Line = 1,
			Side = "right",
			Author = "reviewer",
			Body = body,
			CreatedAt = "now",
			InReplyTo = 0,
		};
	}

	private sealed class GatedReviewComments : IReviewCommentStore {
		private readonly TaskCompletionSource _allowAdd = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _addCalls;
		private int _completedAdds;

		public TaskCompletionSource AddStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AddCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int AddCalls => Volatile.Read(ref _addCalls);
		public int CompletedAdds => Volatile.Read(ref _completedAdds);

		public Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<ReviewComment>>([]);

		public async Task<ReviewComment> AddAsync(
			RepoRef repo,
			int number,
			string commitId,
			NewReviewComment draft,
			CancellationToken ct = default) {
			Interlocked.Increment(ref _addCalls);
			AddStarted.TrySetResult();
			try {
				await _allowAdd.Task.WaitAsync(ct);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				AddCanceled.TrySetResult();
				throw;
			}

			Interlocked.Increment(ref _completedAdds);
			return Comment(draft.Body);
		}

		public Task<ReviewComment> ReplyAsync(
			RepoRef repo,
			int number,
			long inReplyTo,
			string body,
			CancellationToken ct = default) => Task.FromResult(Comment(body));

		private static ReviewComment Comment(string body) => new() {
			Id = 1,
			Path = "readme.txt",
			Line = 1,
			Side = "right",
			Author = "reviewer",
			Body = body,
			CreatedAt = "now",
			InReplyTo = 0,
		};
	}
}
