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
	}

	[Fact]
	public async Task CleanExit_OnFailure_DoesNotRestart() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.NotifyExited(0);

		Assert.Equal(SupervisorState.Idle, h.Sup.State);
		Assert.Contains(h.Changes, c => c.State == SupervisorState.Idle && c.ExitCode == 0);
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount); // never relaunched
	}

	[Fact]
	public async Task Crash_OnFailure_RestartsAfterBackoff() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.NotifyExited(1);

		Assert.Equal(SupervisorState.BackingOff, h.Sup.State);
		Assert.Equal(1, h.StartCount); // not yet — still backing off

		h.Clock.Advance(TimeSpan.FromMilliseconds(100));

		Assert.True(await h.WaitStartAsync());
		Assert.Equal(2, h.StartCount);
		Assert.Equal(1, h.Starts[1]); // attempt index 1
		Assert.Equal(1, h.Sup.RestartCount);
		Assert.Equal(SupervisorState.Running, h.Sup.State);
	}

	[Fact]
	public async Task Never_DoesNotRestartOnCrash() {
		using var h = new Harness(Opts(RestartPolicy.Never));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.NotifyExited(1);

		Assert.Equal(SupervisorState.Idle, h.Sup.State);
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
	}

	[Fact]
	public async Task Always_RestartsOnCleanExit() {
		using var h = new Harness(Opts(RestartPolicy.Always, initialMs: 100));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.NotifyExited(0); // Always relaunches even on clean exit
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
		h.Sup.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(99));
		Assert.Equal(1, h.StartCount); // not due yet
		h.Clock.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(await h.WaitStartAsync());

		// Second consecutive crash: doubled to 200ms.
		h.Sup.NotifyExited(1);
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
		h.Sup.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));
		Assert.True(await h.WaitStartAsync());

		// A 2s healthy run before the next crash resets the count.
		h.Clock.Advance(TimeSpan.FromMilliseconds(2000));
		h.Sup.NotifyExited(1);

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
		h.Sup.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(100));
		Assert.True(await h.WaitStartAsync());

		// Second consecutive crash: grown would be 200ms but the cap clamps it to 150ms.
		h.Sup.NotifyExited(1);
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
		h.Sup.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());

		h.Sup.NotifyExited(1);
		h.Clock.Advance(TimeSpan.FromMilliseconds(10));
		Assert.True(await h.WaitStartAsync());

		// The third crash trips the breaker instead of restarting.
		h.Sup.NotifyExited(1);

		Assert.Equal(SupervisorState.Failed, h.Sup.State);
		Assert.Contains(h.Changes, c => c.State == SupervisorState.Failed && c.ExitCode == 1);
		await Task.Delay(100);
		Assert.Equal(3, h.StartCount); // 1 initial + 2 restarts, then gave up
		Assert.Equal(2, h.Sup.RestartCount);
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
		h.Sup.NotifyExited(1);
		await Task.Delay(100);
		Assert.Equal(1, h.StartCount);
		Assert.Equal(SupervisorState.Idle, h.Sup.State);
	}

	[Fact]
	public async Task Stop_CancelsPendingBackoff() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure, initialMs: 1000));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.NotifyExited(1);
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
	public async Task Dispose_StopsAndIgnoresFurtherExits() {
		using var h = new Harness(Opts(RestartPolicy.OnFailure));
		h.Sup.Start();
		Assert.True(await h.WaitStartAsync());

		h.Sup.Dispose();
		Assert.Equal(1, h.Stops);

		h.Sup.NotifyExited(1); // ignored after dispose
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
		int maxRestarts = 5) =>
		new() {
			Policy = policy,
			InitialBackoff = TimeSpan.FromMilliseconds(initialMs),
			BackoffMultiplier = mult,
			MaxBackoff = TimeSpan.FromMilliseconds(maxMs),
			HealthyAfter = TimeSpan.FromMilliseconds(healthyMs),
			CrashLoopWindow = TimeSpan.FromMilliseconds(windowMs),
			MaxRestartsInWindow = maxRestarts,
		};

	/// <summary>A supervisor wired to recording start/stop delegates and a manually-advanced clock.</summary>
	private sealed class Harness : IDisposable {
		public readonly FakeSupervisorClock Clock = new();
		public readonly List<int> Starts = [];
		public readonly List<SupervisorStateChanged> Changes = [];
		public int Stops;

		private readonly SemaphoreSlim _started = new(0);
		private readonly HashSet<int> _throwOn;
		private readonly object _gate = new();

		public Harness(SupervisionOptions options, params int[] throwOnAttempts) {
			_throwOn = [.. throwOnAttempts];
			Sup = new ProcessSupervisor("test", OnStart, OnStop, options, log: null, clock: Clock);
			Sup.StateChanged += c => {
				lock (_gate) {
					Changes.Add(c);
				}
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

		public void Dispose() => Sup.Dispose();

		private void OnStart(int attempt) {
			if (_throwOn.Contains(attempt)) {
				throw new InvalidOperationException($"boom on attempt {attempt}");
			}

			lock (_gate) {
				Starts.Add(attempt);
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
