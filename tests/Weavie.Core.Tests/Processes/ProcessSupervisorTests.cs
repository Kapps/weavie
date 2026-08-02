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
		using var h = new Harness(Opts(RestartPolicy.OnFailure));

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
		using var h = new Harness(Opts(RestartPolicy.OnFailure));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
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
		using var h = new Harness(Opts(
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, healthyMs: 1_000));
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
		using var supervisor = new ProcessSupervisor(
			"test",
			_ => {
				Interlocked.Increment(ref startCount);
				starts.Release();
			},
			() => Interlocked.Increment(ref stops),
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
		using var supervisor = new ProcessSupervisor(
			"test",
			_ => { },
			() => {
				if (Interlocked.Increment(ref stopCalls) == 1) {
					stopEntered.TrySetResult();
					releaseStop.Task.GetAwaiter().GetResult();
				}
			},
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
		try {
			await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			supervisor.Stop();
			Assert.Equal(SupervisorState.Idle, supervisor.State);
		} finally {
			releaseStop.TrySetResult();
		}

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
		using var supervisor = new ProcessSupervisor(
			"test",
			current => launch = current,
			() => { },
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
		supervisor.Dispose();

		releaseRunning.TrySetResult();
		await supervisor.DrainObserversAsync().WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(0, Volatile.Read(ref backingOffObserved));
	}

	[Fact]
	public async Task UnsubscribedObserverIsSkippedWhileItsNotificationIsQueued() {
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int removedObserverCalls = 0;
		using var supervisor = new ProcessSupervisor(
			"test",
			_ => { },
			() => { },
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure));

		Assert.False(h.Sup.ReportUnhealthy(1, "nothing is running"));
		Assert.Equal(0, h.Stops);

		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		h.Sup.Stop();
		Assert.False(h.Sup.ReportUnhealthy(h.Sup.Generation, "already stopped"));
		Assert.Equal(1, h.Stops);
	}

	[Fact]
	public async Task UnhealthyReport_ForAStaleGeneration_DoesNotStopItsReplacement() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
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
		using var h = new Harness(Opts(RestartPolicy.Never));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(1);

		Assert.Equal(SupervisorState.Idle, h.Sup.State);
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
	}

	[Fact]
	public async Task Always_RestartsOnCleanExit() {
		using var h = new Harness(Opts(RestartPolicy.Always, initialMs: 100));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100, mult: 2));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100, mult: 2, healthyMs: 1000));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100, mult: 2, maxMs: 150));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 10, maxRestarts: 2));
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 10, maxRestarts: 2));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		// Use up both permitted restarts inside the crash-loop window.
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());
		h.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());

		h.Sup.Stop();
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
		using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.Stop();

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
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 1000));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.NotifyExited(1);
		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);

		h.Sup.Stop();
		h.Clock.Advance(TimeSpan.FromMilliseconds(2000)); // the scheduled restart must not fire

		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
		Assert.Equal(SupervisorState.Idle, h.Sup.State);
	}

	[Fact]
	public async Task LaunchException_CountsAsCrash_AndRestarts() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100), throwOnAttempts: 0);

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
		using var h = new Harness(Opts(RestartPolicy.Always, initialMs: 10));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());
		var predecessor = h.LastLaunch;

		// Stop kills the child, but its exit event hasn't been delivered yet when Start launches a replacement.
		h.Sup.Stop();
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
	public async Task Dispose_StopsAndIgnoresFurtherExits() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.Dispose();
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
	private sealed class Harness : IDisposable {
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

		public void Dispose() => Sup.Dispose();

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

		private void OnStop() {
			lock (_gate) {
				Stops++;
			}
		}
	}

	/// <summary>A clock whose delays complete only when the test advances time past their due point.</summary>
	private sealed class FakeSupervisorClock : ISupervisorClock {
		private readonly object _gate = new();
		private readonly List<Pending> _pending = [];
		private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

		public DateTimeOffset UtcNow {
			get {
				lock (_gate) {
					return _now;
				}
			}
		}

		public Task Delay(TimeSpan delay, CancellationToken cancellationToken) {
			if (delay <= TimeSpan.Zero) {
				return Task.CompletedTask;
			}

			var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (_gate) {
				_pending.Add(new Pending(_now + delay, tcs));
			}

			cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
			return tcs.Task;
		}

		public void Advance(TimeSpan by) {
			var due = new List<TaskCompletionSource>();
			lock (_gate) {
				_now += by;
				for (int i = _pending.Count - 1; i >= 0; i--) {
					if (_pending[i].Due <= _now) {
						due.Add(_pending[i].Tcs);
						_pending.RemoveAt(i);
					}
				}
			}

			foreach (var tcs in due) {
				tcs.TrySetResult();
			}
		}

		private readonly record struct Pending(DateTimeOffset Due, TaskCompletionSource Tcs);
	}
}
