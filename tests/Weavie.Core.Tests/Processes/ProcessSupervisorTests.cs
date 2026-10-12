using System.Collections.Concurrent;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Restart-policy state machine: clean exits vs crashes, exponential backoff, healthy-run reset, the
/// crash-loop breaker, and intentional stop/dispose. A fake clock keeps backoff timing deterministic.
/// </summary>
public sealed class ProcessSupervisorTests {
	[Fact]
	public async Task Start_LaunchesOnce_AndIsRunning() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure));

		h.Sup.Start();

		Assert.True(await h.WaitStartAsync());
		Assert.Equal(1, h.StartCount);
		Assert.Equal(0, h.Starts[0]);
		Assert.Equal(SupervisorState.Running, h.Sup.State);
		Assert.Equal(0, h.Sup.RestartCount);
		Assert.Equal(1, h.Sup.Generation);
	}

	[Fact]
	public async Task CleanExit_OnFailure_DoesNotRestart() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(0);

		Assert.Equal(SupervisorState.Idle, h.Sup.State);
		Assert.True(await h.WaitChangeAsync(c => c.State == SupervisorState.Idle && c.ExitCode == 0));
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount); // never relaunched
	}

	[Fact]
	public async Task Crash_OnFailure_RestartsAfterBackoff() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(1);

		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);
		Assert.Equal(1, h.StartCount); // not yet — still backing off

		h.Clock.Advance(TimeSpan.FromMilliseconds(100));

		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);
		Assert.Equal(1, h.Starts[1]); // attempt index 1
		Assert.Equal(1, h.Sup.RestartCount);
		Assert.Equal(2, h.Sup.Generation);
		Assert.Equal(SupervisorState.Running, h.Sup.State);
	}

	[Fact]
	public async Task UnhealthyRunningGeneration_IsStoppedAndRestartedAsAFailure() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		Assert.True(h.Sup.ReportUnhealthy(h.Sup.Generation, "health probe reported a timed-out message"));

		Assert.Equal(1, h.Stops);
		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);
		Assert.Equal(1, h.Sup.RestartCount);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UnconfirmedGenerationsTripTheConsecutiveBreakerAcrossLongRuns(bool reportUnhealthy) {
		await using var h = new Harness(Opts(
			RestartPolicy.OnFailure,
			initialMs: 100,
			healthyMs: 1_000,
			windowMs: 100,
			maxConsecutive: 2,
			requireExplicitHealth: true));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		for (int failure = 0; failure < 2; failure++) {
			h.Clock.Advance(TimeSpan.FromSeconds(2));
			Fail(failure);
			h.Clock.Advance(TimeSpan.FromMilliseconds(100));
			Assert.True(await h.WaitStartAsync());
		}

		h.Clock.Advance(TimeSpan.FromSeconds(2));
		Fail(2);
		Assert.Equal(SupervisorState.Failed, h.Sup.State);

		void Fail(int failure) {
			if (reportUnhealthy) {
				Assert.True(h.Sup.ReportUnhealthy(h.Sup.Generation, $"failed health probe {failure}"));
			} else {
				h.NotifyExited(1);
			}
		}
	}

	[Fact]
	public async Task ReportHealthyRequiresTheCurrentGenerationToClearProbation() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, healthyMs: 1_000));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		long generation = h.Sup.Generation;

		Assert.False(h.Sup.ReportHealthy(generation));
		h.Clock.Advance(TimeSpan.FromSeconds(1));
		Assert.True(h.Sup.ReportHealthy(generation));
		Assert.False(h.Sup.ReportHealthy(generation + 1));
	}

	[Fact]
	public async Task BlockingObserversAndLogsCannotPreventUnhealthyReplacement() {
		var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var logEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseDiagnostics = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var starts = new SemaphoreSlim(0);
		int startCount = 0;
		int stops = 0;
		var clock = new FakeSupervisorClock();
		await using var supervisor = new ProcessSupervisor(
			"test",
			_ => {
				Interlocked.Increment(ref startCount);
				starts.Release();
			},
			() => ProcessStop.Immediately(() => Interlocked.Increment(ref stops)),
			Opts(RestartPolicy.OnFailure, initialMs: 100),
			entry => {
				if (entry.Message.Contains("unhealthy generation", StringComparison.Ordinal)) {
					logEntered.TrySetResult();
					releaseDiagnostics.Task.GetAwaiter().GetResult();
					throw new InvalidOperationException("diagnostic sink failed");
				}
			},
			clock);
		supervisor.StateChanged += change => {
			if (change.State == SupervisorState.BackingOff) {
				observerEntered.TrySetResult();
				releaseDiagnostics.Task.GetAwaiter().GetResult();
			}
		};

		try {
			supervisor.Start();
			Assert.True(await starts.WaitAsync(TimeSpan.FromSeconds(2)));
			Assert.True(supervisor.ReportUnhealthy(supervisor.Generation, "stuck worker"));
			await observerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			await logEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			Assert.Equal(SupervisorState.BackingOff, supervisor.State);
			Assert.Equal(1, Volatile.Read(ref stops));

			clock.Advance(TimeSpan.FromMilliseconds(100));
			Assert.True(await starts.WaitAsync(TimeSpan.FromSeconds(2)));
			Assert.Equal(2, Volatile.Read(ref startCount));
			Assert.Equal(SupervisorState.Running, supervisor.State);
		} finally {
			releaseDiagnostics.TrySetResult();
		}
	}

	[Fact]
	public async Task ConcurrentStopCannotReorderABlockedUnhealthyTransition() {
		var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var observed = new ConcurrentQueue<SupervisorState>();
		var threeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int stopCalls = 0;
		await using var supervisor = new ProcessSupervisor(
			"test",
			_ => { },
			() => ProcessStop.Immediately(() => {
				if (Interlocked.Increment(ref stopCalls) == 1) {
					stopEntered.TrySetResult();
					releaseStop.Task.GetAwaiter().GetResult();
				}
			}),
			Opts(RestartPolicy.OnFailure),
			log: null,
			clock: new FakeSupervisorClock());
		supervisor.StateChanged += change => {
			observed.Enqueue(change.State);
			if (observed.Count >= 3) {
				threeObserved.TrySetResult();
			}
		};
		supervisor.Start();

		var unhealthy = Task.Run(() => supervisor.ReportUnhealthy(supervisor.Generation, "stuck worker"));
		Task stopping;
		try {
			await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			stopping = supervisor.StopAsync();
			Assert.Equal(SupervisorState.Idle, supervisor.State);
			Assert.False(stopping.IsCompleted); // queued behind the blocked unhealthy stop
		} finally {
			releaseStop.TrySetResult();
		}

		await stopping.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(2, Volatile.Read(ref stopCalls));

		Assert.True(await unhealthy.WaitAsync(TimeSpan.FromSeconds(2)));
		await threeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(
			[SupervisorState.Running, SupervisorState.BackingOff, SupervisorState.Idle],
			observed.Take(3));
	}

	[Fact]
	public async Task DisposeSuppressesQueuedObserverCallbacks() {
		var runningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		SupervisedLaunch? launch = null;
		int backingOffObserved = 0;
		await using var supervisor = new ProcessSupervisor(
			"test",
			current => launch = current,
			() => ProcessStop.Immediately(() => { }),
			Opts(RestartPolicy.OnFailure),
			log: null,
			clock: new FakeSupervisorClock());
		supervisor.StateChanged += change => {
			if (change.State == SupervisorState.Running) {
				runningEntered.TrySetResult();
				releaseRunning.Task.GetAwaiter().GetResult();
			} else if (change.State == SupervisorState.BackingOff) {
				Interlocked.Exchange(ref backingOffObserved, 1);
			}
		};
		supervisor.Start();
		await runningEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		launch!.NotifyExited(1);
		await supervisor.DisposeAsync();

		releaseRunning.TrySetResult();
		await supervisor.DrainObserversAsync().WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(0, Volatile.Read(ref backingOffObserved));
	}

	[Fact]
	public async Task UnsubscribedObserverIsSkippedWhileItsNotificationIsQueued() {
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int removedObserverCalls = 0;
		await using var supervisor = new ProcessSupervisor(
			"test",
			_ => { },
			() => ProcessStop.Immediately(() => { }),
			Opts(RestartPolicy.OnFailure),
			log: null,
			clock: new FakeSupervisorClock());
		void BlockingObserver(SupervisorStateChanged _) {
			firstEntered.TrySetResult();
			releaseFirst.Task.GetAwaiter().GetResult();
		}
		void RemovedObserver(SupervisorStateChanged _) => Interlocked.Increment(ref removedObserverCalls);
		supervisor.StateChanged += BlockingObserver;
		supervisor.StateChanged += RemovedObserver;
		supervisor.Start();
		await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

		supervisor.StateChanged -= RemovedObserver;
		releaseFirst.TrySetResult();
		await supervisor.DrainObserversAsync().WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(0, Volatile.Read(ref removedObserverCalls));
	}

	[Fact]
	public async Task UnhealthyReport_IsIgnoredWhenNoGenerationIsRunning() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure));

		Assert.False(h.Sup.ReportUnhealthy(1, "nothing is running"));
		Assert.Equal(0, h.Stops);

		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		await h.Sup.StopAsync();
		Assert.False(h.Sup.ReportUnhealthy(h.Sup.Generation, "already stopped"));
		Assert.Equal(1, h.Stops);
	}

	[Fact]
	public async Task UnhealthyReport_ForAStaleGeneration_DoesNotStopItsReplacement() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		long staleGeneration = h.Sup.Generation;
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));
		Assert.True(await h.WaitStartAsync());

		Assert.False(h.Sup.ReportUnhealthy(staleGeneration, "late health result"));
		Assert.Equal(0, h.Stops);
		Assert.Equal(SupervisorState.Running, h.Sup.State);
	}

	[Fact]
	public async Task Never_DoesNotRestartOnCrash() {
		await using var h = new Harness(Opts(RestartPolicy.Never));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(1);

		Assert.Equal(SupervisorState.Idle, h.Sup.State);
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
	}

	[Fact]
	public async Task Always_RestartsOnCleanExit() {
		await using var h = new Harness(Opts(RestartPolicy.Always, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(0); // Always relaunches even on clean exit
		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);

		h.Clock.Advance(TimeSpan.FromMilliseconds(100));

		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);
	}

	[Fact]
	public async Task Backoff_GrowsPerConsecutiveCrash() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100, mult: 2));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// First crash: 100ms backoff.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(99));
		Assert.Equal(1, h.StartCount); // not due yet
		h.Clock.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(await h.WaitStartAsync());

		// Second consecutive crash: doubled to 200ms.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(199));
		Assert.Equal(2, h.StartCount); // 200ms not elapsed
		h.Clock.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(3, h.StartCount);
	}

	[Fact]
	public async Task HealthyRun_ResetsBackoff() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100, mult: 2, healthyMs: 1000));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// One crash grows the consecutive count (next backoff would be 200ms).
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));
		Assert.True(await h.WaitStartAsync());

		// A 2s healthy run before the next crash resets the count.
		h.Clock.Advance(TimeSpan.FromMilliseconds(2000));
		h.NotifyExited(1);

		// Backoff is back to the initial 100ms, not 200ms.
		h.Clock.Advance(TimeSpan.FromMilliseconds(99));
		Assert.Equal(2, h.StartCount);
		h.Clock.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(3, h.StartCount);
	}

	[Fact]
	public async Task Backoff_IsCappedAtMaxBackoff() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100, mult: 2, maxMs: 150));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// First crash: 100ms (under the cap).
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));
		Assert.True(await h.WaitStartAsync());

		// Second consecutive crash: grown would be 200ms but the cap clamps it to 150ms.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(149));
		Assert.Equal(2, h.StartCount); // not yet
		h.Clock.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(3, h.StartCount); // fired at the cap, not at 200ms
	}

	[Fact]
	public async Task CrashLoop_TripsBreakerAfterMaxRestarts() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 10, maxRestarts: 2));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// Two restarts are permitted.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());

		// The third crash trips the breaker instead of restarting.
		h.NotifyExited(1);

		Assert.Equal(SupervisorState.Failed, h.Sup.State);
		Assert.True(await h.WaitChangeAsync(c => c.State == SupervisorState.Failed && c.ExitCode == 1));
		await Task.Delay(100);
		Assert.Equal(3, h.StartCount); // 1 initial + 2 restarts, then gave up
		Assert.Equal(2, h.Sup.RestartCount);
	}

	[Fact]
	public async Task StopThenStart_ClearsCrashHistory() {
		// The update rollback path relies on this: Stop() → Start() onto the known-good build must not
		// inherit the bad build's crashes, or the breaker would insta-trip on the rollback.
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 10, maxRestarts: 2));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// Use up both permitted restarts inside the crash-loop window.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());

		await h.Sup.StopAsync();
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// With history cleared, a crash restarts instead of tripping the breaker.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());
		Assert.NotEqual(SupervisorState.Failed, h.Sup.State);
	}

	[Fact]
	public async Task Stop_StopsInstance_AndSuppressesRestart() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		await h.Sup.StopAsync();

		Assert.Equal(1, h.Stops);
		Assert.Equal(SupervisorState.Idle, h.Sup.State);

		// The kill's exit must not count as a crash.
		h.NotifyExited(1);
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
		Assert.Equal(SupervisorState.Idle, h.Sup.State);
	}

	[Fact]
	public async Task Stop_CancelsPendingBackoff() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 1000));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(1);
		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);

		await h.Sup.StopAsync();
		h.Clock.Advance(TimeSpan.FromMilliseconds(2000)); // the scheduled restart must not fire

		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
		Assert.Equal(SupervisorState.Idle, h.Sup.State);
	}

	[Fact]
	public async Task LaunchException_CountsAsCrash_AndRestarts() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100), throwOnAttempts: 0);

		h.Sup.Start(); // attempt 0 throws inside the start delegate

		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);
		Assert.Equal(0, h.StartCount); // throwing launch recorded nothing

		h.Clock.Advance(TimeSpan.FromMilliseconds(100));

		Assert.True(await h.WaitStartAsync());
		Assert.Equal(1, h.StartCount);
		Assert.Equal(1, h.Starts[0]); // the successful relaunch was attempt 1
		Assert.Equal(SupervisorState.Running, h.Sup.State);
	}

	[Fact]
	public async Task StaleExit_AfterStopStart_DoesNotRestartReplacement() {
		await using var h = new Harness(Opts(RestartPolicy.Always, initialMs: 10));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		var predecessor = h.LastLaunch;

		// Stop kills the child, but its exit event hasn't been delivered yet when Start launches a replacement.
		await h.Sup.StopAsync();
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);

		// The killed predecessor's late exit must not be attributed to the healthy replacement.
		predecessor.NotifyExited(137);
		Assert.Equal(SupervisorState.Running, h.Sup.State);
		h.Clock.Advance(TimeSpan.FromMilliseconds(1000));
		await Task.Delay(100);
		Assert.Equal(2, h.StartCount); // no duplicate launch

		// The replacement's own exit is still handled normally.
		h.NotifyExited(1);
		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);
	}

	[Fact]
	public async Task Start_DuringAnInFlightStop_LaunchesOnlyOnceTheStopCompletes() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		h.HeldStop = new TaskCompletionSource<ProcessStopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

		var stopping = h.Sup.StopAsync();
		h.Sup.Start();

		Assert.False(await h.WaitStartAsync(timeoutMs: 100));
		Assert.Equal(SupervisorState.Running, h.Sup.State);
		h.HeldStop.SetResult(ProcessStopOutcome.Graceful);
		await stopping.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);
	}

	[Fact]
	public async Task StopDuringALaunchInFlight_RunsOnlyOnceTheLaunchReturns() {
		var launching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int stops = 0;
		await using var supervisor = new ProcessSupervisor(
			"test",
			_ => {
				launching.TrySetResult();
				releaseLaunch.Task.GetAwaiter().GetResult();
			},
			() => ProcessStop.Immediately(() => Interlocked.Increment(ref stops)),
			Opts(RestartPolicy.Never),
			log: null,
			new FakeSupervisorClock());
		var starting = Task.Run(supervisor.Start);
		await launching.Task.WaitAsync(TimeSpan.FromSeconds(2));

		var stopping = supervisor.StopAsync();
		await Task.Delay(50);
		Assert.Equal(0, Volatile.Read(ref stops));
		releaseLaunch.SetResult();

		await stopping.WaitAsync(TimeSpan.FromSeconds(2));
		await starting;
		Assert.Equal(1, Volatile.Read(ref stops));
	}

	[Fact]
	public async Task RestartAfterUnhealthy_WaitsForTheUnhealthyInstanceToStop() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		h.HeldStop = new TaskCompletionSource<ProcessStopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

		Assert.True(h.Sup.ReportUnhealthy(h.Sup.Generation, "wedged"));
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));

		Assert.False(await h.WaitStartAsync(timeoutMs: 100));
		h.HeldStop.SetResult(ProcessStopOutcome.Graceful);
		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);
	}

	[Fact]
	public async Task ForcedStop_LogsAWarningAndRaisesStopped() {
		var logged = new ConcurrentQueue<SupervisorLogEntry>();
		var stopped = new TaskCompletionSource<ProcessStopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
		var forced = ProcessStopOutcome.Forced("didn't exit within 5s of closing its input");
		await using var supervisor = new ProcessSupervisor(
			"test", _ => { }, () => Task.FromResult(forced), Opts(RestartPolicy.Never), logged.Enqueue, new FakeSupervisorClock());
		supervisor.ForceStopped += outcome => stopped.TrySetResult(outcome);
		supervisor.Start();

		await supervisor.StopAsync();

		Assert.Same(forced, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		await Wait.UntilAsync(() => logged.Any(entry => entry.Level == SupervisorLogLevel.Warning
			&& entry.Message.Contains("didn't exit within 5s", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task ForcedStopDuringDispose_StillRaisesStopped() {
		var stopped = new TaskCompletionSource<ProcessStopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
		var supervisor = new ProcessSupervisor(
			"test", _ => { }, () => Task.FromResult(ProcessStopOutcome.Forced("didn't exit")), Opts(RestartPolicy.Never),
			log: null, new FakeSupervisorClock());
		supervisor.ForceStopped += outcome => stopped.TrySetResult(outcome);
		supervisor.Start();

		await supervisor.DisposeAsync();

		Assert.True((await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2))).WasForced);
	}

	[Fact]
	public async Task Dispose_StopsAndIgnoresFurtherExits() {
		await using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		await h.Sup.DisposeAsync();
		Assert.Equal(1, h.Stops);

		h.NotifyExited(1); // ignored after dispose
		h.Sup.Start();         // no-op after dispose
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
	}

	private static SupervisionOptions Opts(
		RestartPolicy policy,
		double initialMs = 100,
		double mult = 1,
		double maxMs = 10_000,
		double healthyMs = 10_000,
		double windowMs = 60_000,
		int maxRestarts = 5,
		int maxConsecutive = 5,
		bool requireExplicitHealth = false) =>
		new() {
			Policy = policy,
			InitialBackoff = TimeSpan.FromMilliseconds(initialMs),
			BackoffMultiplier = mult,
			MaxBackoff = TimeSpan.FromMilliseconds(maxMs),
			HealthyAfter = TimeSpan.FromMilliseconds(healthyMs),
			CrashLoopWindow = TimeSpan.FromMilliseconds(windowMs),
			MaxRestartsInWindow = maxRestarts,
			MaxConsecutiveFailures = maxConsecutive,
			RequireExplicitHealth = requireExplicitHealth,
		};

	/// <summary>A supervisor wired to recording start/stop delegates and a manually-advanced clock.</summary>
	private sealed class Harness : IAsyncDisposable {
		public readonly FakeSupervisorClock Clock = new();
		public readonly List<int> Starts = [];
		public readonly List<SupervisorStateChanged> Changes = [];
		public int Stops;

		private readonly SemaphoreSlim _started = new(0);
		private readonly SemaphoreSlim _changed = new(0);
		private readonly HashSet<int> _throwOn;
		private readonly object _gate = new();
		private readonly List<SupervisedLaunch> _launches = [];

		public Harness(SupervisionOptions options, params int[] throwOnAttempts) {
			_throwOn = [.. throwOnAttempts];
			Sup = new ProcessSupervisor("test", OnStart, OnStop, options, log: null, clock: Clock);
			Sup.StateChanged += c => {
				lock (_gate) {
					Changes.Add(c);
				}
				_changed.Release();
			};
		}

		public ProcessSupervisor Sup { get; }

		public int StartCount {
			get {
				lock (_gate) {
					return Starts.Count;
				}
			}
		}

		public Task<bool> WaitStartAsync(int timeoutMs = 5000) => _started.WaitAsync(timeoutMs);

		public async Task<bool> WaitChangeAsync(Func<SupervisorStateChanged, bool> predicate) {
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			while (true) {
				lock (_gate) {
					if (Changes.Any(predicate)) {
						return true;
					}
				}

				try {
					await _changed.WaitAsync(timeout.Token);
				} catch (OperationCanceledException) {
					return false;
				}
			}
		}

		public ValueTask DisposeAsync() => Sup.DisposeAsync();

		/// <summary>While set, every stop waits for the test to complete it with an outcome.</summary>
		public TaskCompletionSource<ProcessStopOutcome>? HeldStop { get; set; }

		/// <summary>The most recently launched instance's handle.</summary>
		public SupervisedLaunch LastLaunch {
			get {
				lock (_gate) {
					return _launches[^1];
				}
			}
		}

		/// <summary>Reports an exit for the most recently launched instance.</summary>
		public void NotifyExited(int exitCode) => LastLaunch.NotifyExited(exitCode);

		private void OnStart(SupervisedLaunch launch) {
			if (_throwOn.Contains(launch.Attempt)) {
				throw new InvalidOperationException($"boom on attempt {launch.Attempt}");
			}

			lock (_gate) {
				Starts.Add(launch.Attempt);
				_launches.Add(launch);
			}

			_started.Release();
		}

		private Task<ProcessStopOutcome> OnStop() {
			lock (_gate) {
				Stops++;
				return HeldStop?.Task ?? Task.FromResult(ProcessStopOutcome.Graceful);
			}
		}
	}
}
