using System.Threading.Channels;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class PullRequestStatusMonitorTests {
	[Fact]
	public async Task ActiveStatus_PollsAndLeavingActiveRunsAFinalProbe() {
		await using var background = new SessionTaskScope(_ => { });
		var delay = new ManualDelay();
		int calls = 0;
		var monitor = new PullRequestStatusMonitor(
			background,
			_ => Task.FromResult(Snapshot(Interlocked.Increment(ref calls))),
			_ => { },
			delay.WaitAsync,
			TimeSpan.FromSeconds(30));

		monitor.UpdateStatus(SessionStatus.Working);
		await Wait.UntilAsync(() => calls == 1);
		var firstPoll = await delay.NextAsync();
		Assert.Equal(TimeSpan.FromSeconds(30), firstPoll.Duration);

		firstPoll.Elapsed.TrySetResult();
		await Wait.UntilAsync(() => calls == 2);
		await delay.NextAsync();
		monitor.UpdateStatus(SessionStatus.NeedsInput);

		await Wait.UntilAsync(() => calls == 3);
	}

	[Fact]
	public async Task RefreshesDuringAProbe_CoalesceIntoOnePendingProbe() {
		await using var background = new SessionTaskScope(_ => { });
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;
		var monitor = new PullRequestStatusMonitor(
			background,
			async ct => {
				if (Interlocked.Increment(ref calls) == 1) {
					firstStarted.TrySetResult();
					await releaseFirst.Task.WaitAsync(ct);
				}

				return Snapshot(calls);
			},
			_ => { },
			Task.Delay,
			TimeSpan.FromSeconds(30));

		monitor.RequestRefresh();
		await firstStarted.Task;
		monitor.RequestRefresh();
		monitor.RequestRefresh();
		Assert.Equal(1, calls);

		releaseFirst.TrySetResult();
		await Wait.UntilAsync(() => calls == 2);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task TransientFailure_PreservesTheLastGoodPullRequestForTheSameBranch() {
		await using var background = new SessionTaskScope(_ => { });
		var snapshots = new Queue<PullRequestStatusSnapshot>([
			new PullRequestStatusSnapshot(
				"feature",
				new PullRequestStatusInfo(123, "https://example.test/pull/123", "open"),
				null),
			new PullRequestStatusSnapshot("feature", null, "network unavailable"),
		]);
		var published = new List<PullRequestStatusSnapshot>();
		var monitor = new PullRequestStatusMonitor(
			background,
			_ => Task.FromResult(snapshots.Dequeue()),
			published.Add,
			Task.Delay,
			TimeSpan.FromSeconds(30));

		monitor.RequestRefresh();
		await Wait.UntilAsync(() => published.Count == 1);
		monitor.RequestRefresh();
		await Wait.UntilAsync(() => published.Count == 2);

		Assert.Equal(123, published[1].PullRequest?.Number);
		Assert.Equal("network unavailable", published[1].Error);
	}

	private static PullRequestStatusSnapshot Snapshot(int number) =>
		new(
			"feature",
			new PullRequestStatusInfo(number, $"https://example.test/pull/{number}", "open"),
			null);

	private sealed class ManualDelay {
		private readonly Channel<DelayCall> _calls = Channel.CreateUnbounded<DelayCall>();

		public async Task WaitAsync(TimeSpan duration, CancellationToken ct) {
			var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var registration = ct.Register(() => elapsed.TrySetCanceled(ct));
			await _calls.Writer.WriteAsync(new DelayCall(duration, elapsed), ct);
			await elapsed.Task;
		}

		public ValueTask<DelayCall> NextAsync() => _calls.Reader.ReadAsync();
	}

	private sealed record DelayCall(TimeSpan Duration, TaskCompletionSource Elapsed);
}
