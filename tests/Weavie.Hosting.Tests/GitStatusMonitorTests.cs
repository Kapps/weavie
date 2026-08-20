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
		var delay = new ManualDelay();
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
			delay.WaitAsync,
			TimeSpan.FromSeconds(10));

		monitor.RequestRefresh();
		await firstStarted.Task;
		monitor.RequestRefresh();
		monitor.RequestRefresh();
		Assert.Equal(1, calls);

		releaseFirst.TrySetResult();
		var cooldown = await delay.NextAsync();
		Assert.Equal(TimeSpan.FromSeconds(10), cooldown.Duration);
		Assert.Equal(1, calls);
		cooldown.Elapsed.TrySetResult();
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
		var delay = new ManualDelay();
		var monitor = new GitStatusMonitor(
			background,
			_ => Task.FromResult(Snapshot(Interlocked.Increment(ref calls))),
			_ => { },
			delay.WaitAsync,
			TimeSpan.FromSeconds(10));

		await monitor.Waiting;
		await Task.Yield();
		Assert.Equal(0, calls);
		Assert.Equal(0, delay.Calls);
	}

	[Fact]
	public async Task EqualRefreshes_AreNotRepublished() {
		await using var background = new SessionTaskScope(_ => { });
		var snapshot = Snapshot(3);
		int calls = 0;
		int publications = 0;
		var delay = new ManualDelay();
		var monitor = new GitStatusMonitor(
			background,
			_ => {
				Interlocked.Increment(ref calls);
				return Task.FromResult(snapshot);
			},
			_ => Interlocked.Increment(ref publications),
			delay.WaitAsync,
			TimeSpan.FromSeconds(10));

		monitor.RequestRefresh();
		await Wait.UntilAsync(() => calls == 1);
		monitor.RequestRefresh();
		var cooldown = await delay.NextAsync();
		Assert.Equal(1, calls);
		cooldown.Elapsed.TrySetResult();
		await Wait.UntilAsync(() => calls == 2);

		Assert.Equal(1, publications);
		Assert.Equal(snapshot, monitor.Latest);
	}

	private static GitStatusSnapshot Snapshot(int added) =>
		new("feature", true, added, 1, null);

	private sealed class ManualDelay {
		private readonly Channel<DelayCall> _calls = Channel.CreateUnbounded<DelayCall>();
		private int _callCount;

		public int Calls => Volatile.Read(ref _callCount);

		public async Task WaitAsync(TimeSpan duration, CancellationToken ct) {
			Interlocked.Increment(ref _callCount);
			var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var registration = ct.Register(() => elapsed.TrySetCanceled(ct));
			await _calls.Writer.WriteAsync(new DelayCall(duration, elapsed), ct);
			await elapsed.Task;
		}

		public ValueTask<DelayCall> NextAsync() => _calls.Reader.ReadAsync();
	}

	private sealed record DelayCall(TimeSpan Duration, TaskCompletionSource Elapsed);
}
