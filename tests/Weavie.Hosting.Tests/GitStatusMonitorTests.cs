using System.Threading.Channels;
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
			published.Add,
			Task.Delay,
			TimeSpan.FromSeconds(1));

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
			_ => { },
			Task.Delay,
			TimeSpan.FromSeconds(1));

		monitor.RequestRefresh();
		await Wait.UntilAsync(() => monitor.Latest is not null);

		Assert.Equal(expected, monitor.Latest);
	}

	[Fact]
	public async Task PollInterval_RefreshesWithoutAnExternalSignal() {
		await using var background = new SessionTaskScope(_ => { });
		var delay = new ManualDelay();
		int calls = 0;
		var monitor = new GitStatusMonitor(
			background,
			_ => Task.FromResult(Snapshot(Interlocked.Increment(ref calls))),
			_ => { },
			delay.WaitAsync,
			TimeSpan.FromSeconds(1));

		var firstPoll = await delay.NextAsync();
		Assert.Equal(TimeSpan.FromSeconds(1), firstPoll.Duration);
		firstPoll.Elapsed.TrySetResult();

		await Wait.UntilAsync(() => calls == 1);
	}

	private static GitStatusSnapshot Snapshot(int added) =>
		new("feature", true, added, 1, null);

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
