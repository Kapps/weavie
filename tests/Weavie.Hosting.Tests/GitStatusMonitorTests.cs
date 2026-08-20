using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class GitStatusMonitorTests {
	[Fact]
	public async Task RefreshesDuringAProbe_CoalesceIntoOneFollowUpProbe() {
		await using var background = new SessionTaskScope(_ => { });
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var published = new List<GitStatusSnapshot>();
		int calls = 0;
		var monitor = new GitStatusMonitor(
			background,
			async ct => {
				int call = Interlocked.Increment(ref calls);
				if (call == 1) {
					firstStarted.TrySetResult();
					await releaseFirst.Task.WaitAsync(ct);
				}

				return Snapshot(call);
			},
			published.Add);

		monitor.RequestRefresh();
		await firstStarted.Task;
		monitor.RequestRefresh();
		monitor.RequestRefresh();
		Assert.Equal(1, calls);

		releaseFirst.TrySetResult();
		await Wait.UntilAsync(() => published.Count == 2);

		Assert.Equal(2, calls);
		Assert.Equal(Snapshot(2), monitor.Latest);
	}

	[Fact]
	public async Task Latest_CachesThePublishedSnapshotForReplay() {
		await using var background = new SessionTaskScope(_ => { });
		var expected = Snapshot(7);
		var monitor = new GitStatusMonitor(
			background,
			_ => Task.FromResult(expected),
			_ => { });

		monitor.RequestRefresh();
		await Wait.UntilAsync(() => monitor.Latest is not null);

		Assert.Equal(expected, monitor.Latest);
	}

	[Fact]
	public async Task IdleMonitor_DoesNotResolveWithoutARefreshRequest() {
		await using var background = new SessionTaskScope(_ => { });
		int calls = 0;
		var monitor = new GitStatusMonitor(
			background,
			_ => Task.FromResult(Snapshot(Interlocked.Increment(ref calls))),
			_ => { });

		await monitor.Waiting;
		await Task.Delay(TimeSpan.FromMilliseconds(1100));
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task EqualRefreshes_AreNotRepublished() {
		await using var background = new SessionTaskScope(_ => { });
		var snapshot = Snapshot(3);
		int calls = 0;
		int publications = 0;
		var monitor = new GitStatusMonitor(
			background,
			_ => {
				Interlocked.Increment(ref calls);
				return Task.FromResult(snapshot);
			},
			_ => Interlocked.Increment(ref publications));

		monitor.RequestRefresh();
		await Wait.UntilAsync(() => calls == 1);
		monitor.RequestRefresh();
		await Wait.UntilAsync(() => calls == 2);

		Assert.Equal(1, publications);
		Assert.Equal(snapshot, monitor.Latest);
	}

	private static GitStatusSnapshot Snapshot(int added) =>
		new("feature", true, added, 1, null);

}
