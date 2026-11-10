using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class ReviewHeadReaderTests {
	private static readonly ReviewContext Review = new(0, "vs main", "topic", "old-base", "", null, "/repo", "main", "topic");

	[Fact]
	public async Task ARebaseFinishingBetweenTheBranchAndMarkerReads_UsesItsRestoredBranch() {
		string? branch = null;
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			_ => Task.FromResult(branch),
			_ => {
				branch = "topic";
				return Task.FromResult(false);
			},
			_ => Task.FromResult<string?>("new-base"),
			CancellationToken.None);

		Assert.Equal(new ReviewHeadSnapshot(Review, "topic", "new-base"), snapshot);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("other")]
	public async Task AStableHeadOutsideTheReviewBranch_ClosesIt(string? branch) {
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			_ => Task.FromResult(branch),
			_ => Task.FromResult(false),
			_ => throw new InvalidOperationException("A different branch has no review merge-base."),
			CancellationToken.None);

		Assert.Equal(new ReviewHeadSnapshot(Review, branch ?? string.Empty, null), snapshot);
	}

	[Fact]
	public async Task AnActiveRebase_LeavesTheReviewWaiting() {
		var snapshot = await HostCore.ReadReviewHeadAsync(
			Review,
			_ => Task.FromResult<string?>(null),
			_ => Task.FromResult(true),
			_ => throw new InvalidOperationException("A rebase in progress has no stable merge-base."),
			CancellationToken.None);

		Assert.Equal(ReviewHeadSnapshot.Waiting, snapshot);
	}
}
