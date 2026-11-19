using Weavie.Core.Git;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class ReviewHeadReaderTests {
	private static readonly ReviewContext Review = new(0, "vs main", "old-base", null, "/repo", "main", "topic");

	[Fact]
	public async Task ARebaseFinishingBetweenTheBranchAndMarkerReads_UsesItsRestoredBranch() {
		string? branch = null;
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			new ListingState(0, 0, 3, false),
			_ => Task.FromResult(branch),
			_ => {
				branch = "topic";
				return Task.FromResult(GitOperation.None);
			},
			_ => Task.FromResult<string?>("new-base"),
			CancellationToken.None);

		Assert.Equal(new ReviewHeadSnapshot(Review, "topic", "new-base", new ListingState(0, 0, 3, false)), snapshot);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("other")]
	public async Task AStableHeadOutsideTheReviewBranch_ClosesIt(string? branch) {
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			default,
			_ => Task.FromResult(branch),
			_ => Task.FromResult(GitOperation.None),
			_ => Task.FromResult<string?>("their-base"),
			CancellationToken.None);

		Assert.Equal(new ReviewHeadSnapshot(Review, branch ?? string.Empty, null, default), snapshot);
	}

	[Fact]
	public async Task AnActiveRebase_LeavesTheReviewWaiting() {
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			default,
			_ => Task.FromResult<string?>(null),
			_ => Task.FromResult(GitOperation.Stopped),
			_ => Task.FromResult<string?>("mid-rebase"),
			CancellationToken.None);

		Assert.Equal(ReviewHeadSnapshot.Waiting, snapshot);
	}

	[Theory]
	[InlineData(true, "old-base")]
	[InlineData(false, "new-base")]
	public async Task AReviewDueForARelist_WaitsWhileAnOperationRewritesTheWorktree(bool stale, string mergeBase) {
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			new ListingState(0, 0, 0, stale),
			_ => Task.FromResult<string?>("topic"),
			_ => Task.FromResult(GitOperation.Stopped),
			_ => Task.FromResult<string?>(mergeBase),
			CancellationToken.None);

		Assert.Equal(ReviewHeadSnapshot.Waiting, snapshot);
	}

	[Fact]
	public async Task AnUpToDateReview_DoesNotAskAboutOperations() {
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			default,
			_ => Task.FromResult<string?>("topic"),
			_ => throw new InvalidOperationException("Nothing to re-list, so nothing waits."),
			_ => Task.FromResult<string?>("old-base"),
			CancellationToken.None);

		Assert.Equal(new ReviewHeadSnapshot(Review, "topic", "old-base", default), snapshot);
	}

	[Fact]
	public async Task AHeldIndexLock_IsWaitedOut() {
		var operations = new Queue<GitOperation>([GitOperation.Locked, GitOperation.Locked, GitOperation.None]);
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			new ListingState(1, 0, 0, true),
			_ => Task.FromResult<string?>("topic"),
			_ => Task.FromResult(operations.Dequeue()),
			_ => Task.FromResult<string?>("old-base"),
			CancellationToken.None);

		Assert.Empty(operations);
		Assert.Equal(new ReviewHeadSnapshot(Review, "topic", "old-base", new ListingState(1, 0, 0, true)), snapshot);
	}
}
