using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class ReviewPublicationTests {
	[Fact]
	public async Task LiveProjectionWaitsForSnapshotBeforeCapturingState() {
		using var publication = new ReviewPublication();
		var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var projections = new List<string>();
		string current = "snapshot";
		var snapshot = publication.RunAsync(async () => {
			projections.Add(current);
			await releaseSnapshot.Task;
		}, CancellationToken.None);
		var live = publication.RunAsync(() => {
			projections.Add(current);
			return Task.CompletedTask;
		}, CancellationToken.None);

		Assert.False(live.IsCompleted);
		current = "latest";
		releaseSnapshot.SetResult();
		await Task.WhenAll(snapshot, live);
		Assert.Equal(["snapshot", "latest"], projections);
	}

	[Fact]
	public async Task CancelledWaitingActionDoesNotMutateReviewState() {
		using var publication = new ReviewPublication();
		using var stopping = new CancellationTokenSource();
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var snapshot = publication.RunAsync(() => release.Task, CancellationToken.None);
		bool mutated = false;
		var action = publication.RunAsync(() => {
			mutated = true;
			return Task.FromResult(42);
		}, stopping.Token);
		await stopping.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
		Assert.False(mutated);
		release.SetResult();
		await snapshot;
		Assert.Equal(42, await publication.RunAsync(() => Task.FromResult(42), CancellationToken.None));
	}
}
