using Weavie.Core.Diagnostics;

namespace Weavie.Core.Processes;

/// <summary>
/// Supervises a single long-lived child: launches it via a caller-supplied <c>start</c> delegate, watches for
/// exit, and relaunches it per a <see cref="RestartPolicy"/> with exponential backoff and a crash-loop breaker.
/// Process-agnostic — a PTY terminal, a <see cref="System.Diagnostics.Process"/>, or anything else is supervised
/// the same way.
/// <para>Thread-safe. <see cref="SupervisedLaunch.NotifyExited"/> may be called from any thread; an exit
/// arriving while not <see cref="SupervisorState.Running"/> is ignored, as is any exit reported for an instance
/// that is no longer the current one (so a stopped predecessor's late exit never restarts its healthy replacement).
/// Stops run one at a time and a launch waits for the stop before it, so two instances never overlap.
/// <see cref="StateChanged"/> and <see cref="Stopped"/> handlers run in order on a separate observer lane.</para>
/// </summary>
public sealed partial class ProcessSupervisor : IAsyncDisposable {
	private readonly Action<SupervisedLaunch> _start;
	private readonly Func<Task<ProcessStopOutcome>> _stop;
	private readonly SupervisionOptions _options;
	private readonly Action<SupervisorLogEntry>? _log;
	private readonly DiagnosticWorker? _logDiagnostics;
	private readonly ISupervisorClock _clock;
	private readonly object _gate = new();
	private readonly Queue<DateTimeOffset> _recentRestarts = new();

	private SupervisorState _state = SupervisorState.Idle;
	private int _attempt;              // launches so far (0 = first launch in flight, grows on restart)
	private SupervisedLaunch? _current; // the live instance's handle; exits from any other handle are stale
	private int _restartCount;         // launches beyond the first
	private long _generation;         // every launch, including an intentional stop/start, gets a new identity
	private int _consecutiveCrashes;   // drives backoff growth; reset by a healthy run
	private DateTimeOffset _startedAt;
	private CancellationTokenSource? _cts;
	private Task _stopping = Task.CompletedTask; // the latest stop; every later stop and launch runs after it
	private Task? _disposal;

	/// <summary>Creates a supervisor. Nothing launches until <see cref="Start"/> is called.</summary>
	/// <param name="name">A short name for logging and status (e.g. <c>"terminal:claude"</c>).</param>
	/// <param name="start">
	/// Launches a fresh instance and wires its exit to the handle's <see cref="SupervisedLaunch.NotifyExited"/>.
	/// An exception thrown here is treated as a crash and feeds the backoff/breaker.
	/// </param>
	/// <param name="stop">
	/// Stops the current instance and completes once it has exited; must be a safe no-op when nothing is running.
	/// </param>
	/// <param name="options">Restart policy and backoff/crash-loop tunables.</param>
	/// <param name="log">Optional sink for structured lifecycle logging.</param>
	/// <param name="clock">Clock for delays and timing; defaults to <see cref="SystemSupervisorClock"/>.</param>
	public ProcessSupervisor(
		string name,
		Action<SupervisedLaunch> start,
		Func<Task<ProcessStopOutcome>> stop,
		SupervisionOptions options,
		Action<SupervisorLogEntry>? log,
		ISupervisorClock? clock) {
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(start);
		ArgumentNullException.ThrowIfNull(stop);
		ArgumentNullException.ThrowIfNull(options);
		if (options.BackoffMultiplier < 1.0) {
			throw new ArgumentOutOfRangeException(nameof(options), "BackoffMultiplier must be >= 1.");
		}

		if (options.MaxRestartsInWindow < 0) {
			throw new ArgumentOutOfRangeException(nameof(options), "MaxRestartsInWindow must be >= 0.");
		}

		if (options.MaxConsecutiveFailures < 0) {
			throw new ArgumentOutOfRangeException(nameof(options), "MaxConsecutiveFailures must be >= 0.");
		}

		Name = name;
		_start = start;
		_stop = stop;
		_options = options;
		_log = log;
		_clock = clock ?? new SystemSupervisorClock();
		_logDiagnostics = log is null
			? null
			: new DiagnosticWorker(message => log(new SupervisorLogEntry(Name, SupervisorLogLevel.Error, message)));
	}

	/// <summary>The supervised process's name.</summary>
	public string Name { get; }

	/// <summary>A snapshot of the current lifecycle state (it can change immediately after you read it).</summary>
	public SupervisorState State {
		get {
			lock (_gate) {
				return _state;
			}
		}
	}

	/// <summary>Total restarts performed since construction.</summary>
	public int RestartCount {
		get {
			lock (_gate) {
				return _restartCount;
			}
		}
	}

	/// <summary>Monotonic identity of the current/most-recent launched generation; zero before the first launch.</summary>
	public long Generation {
		get {
			lock (_gate) {
				return _generation;
			}
		}
	}

	/// <summary>
	/// Launches the process (attempt 0) once any in-flight stop has finished. No-op if it is already running or
	/// scheduled to restart, or if the supervisor has been disposed.
	/// </summary>
	public void Start() {
		SupervisedLaunch launch;
		Task stopping;
		lock (_gate) {
			if (_disposal is not null || _state is SupervisorState.Running or SupervisorState.BackingOff) {
				return;
			}

			_cts?.Dispose();
			_cts = new CancellationTokenSource();
			_state = SupervisorState.Running;
			_attempt = 0;
			_startedAt = _clock.UtcNow;
			// The handle becomes current in the same lock hold that flips to Running, so a predecessor's late
			// exit can never slip through the Running check while still matching _current.
			_generation++;
			launch = new SupervisedLaunch(this, 0, _generation);
			_current = launch;
			stopping = _stopping;
			QueueStateChangedLocked(new SupervisorStateChanged(SupervisorState.Running, null, _restartCount));
		}

		Log(SupervisorLogLevel.Info, "starting");
		LaunchAfter(stopping, launch);
	}

	internal void NotifyLaunchExited(SupervisedLaunch launch, int exitCode) =>
		OnInstanceEnded(launch, exitCode, stopCurrent: false);

	/// <summary>
	/// Replaces a running instance that is alive but no longer healthy. The replacement is recorded as a failed
	/// generation, so it follows the same backoff and crash-loop breaker as a non-zero process exit, and launches
	/// only after the unhealthy instance has been stopped.
	/// </summary>
	/// <returns><c>true</c> when the current running generation was reported; otherwise <c>false</c>.</returns>
	public bool ReportUnhealthy(long generation, string reason) {
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
		ArgumentException.ThrowIfNullOrEmpty(reason);
		SupervisedLaunch launch;
		lock (_gate) {
			if (_disposal is not null
				|| _state != SupervisorState.Running
				|| _generation != generation
				|| _current is null) {
				return false;
			}

			launch = _current;
		}

		bool reported = OnInstanceEnded(launch, exitCode: -1, stopCurrent: true);
		if (reported) {
			Log(SupervisorLogLevel.Error, $"unhealthy generation: {reason}");
		}

		return reported;
	}

	/// <summary>
	/// Records that the current generation has stayed responsive through its configured healthy window.
	/// Returns <c>false</c> for a stale, stopped, or still-probationary generation.
	/// </summary>
	public bool ReportHealthy(long generation) {
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
		lock (_gate) {
			if (_disposal is not null
				|| _state != SupervisorState.Running
				|| _generation != generation
				|| _clock.UtcNow - _startedAt < _options.HealthyAfter) {
				return false;
			}

			_consecutiveCrashes = 0;
			return true;
		}
	}

	/// <summary>
	/// Stops the process and cancels any pending restart; the resulting exit is treated as intentional and is
	/// not relaunched. Completes once the instance has exited. Safe to call repeatedly.
	/// </summary>
	public Task StopAsync() {
		TaskCompletionSource done;
		Task previous;
		lock (_gate) {
			if (_disposal is not null || _state == SupervisorState.Idle) {
				return _stopping;
			}

			_cts?.Cancel();
			_state = SupervisorState.Idle;
			_current = null; // the instance being stopped is no longer anyone's current — its exit is stale
			_consecutiveCrashes = 0;
			_recentRestarts.Clear();
			QueueStateChangedLocked(new SupervisorStateChanged(SupervisorState.Idle, null, _restartCount));
			previous = BeginStopLocked(out done);
		}

		Log(SupervisorLogLevel.Info, "stopping");
		return RunStopAsync(previous, done);
	}

	/// <summary>Stops the process (no relaunch), waits for it to exit, and releases resources. Idempotent.</summary>
	public ValueTask DisposeAsync() {
		TaskCompletionSource done;
		Task previous;
		Task disposal;
		lock (_gate) {
			if (_disposal is not null) {
				return new ValueTask(_disposal);
			}

			CloseStateNotificationsLocked();
			_cts?.Cancel();
			_state = SupervisorState.Idle;
			_current = null;
			previous = BeginStopLocked(out done);
			_disposal = disposal = done.Task.ContinueWith(_ => _cts?.Dispose(), CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		_ = RunStopAsync(previous, done);
		return new ValueTask(disposal);
	}

	private Task BeginStopLocked(out TaskCompletionSource done) {
		var previous = _stopping;
		done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_stopping = done.Task;
		return previous;
	}

	// Runs the stop delegate after the previous stop; never faults, so launches chained behind it always proceed.
	private async Task RunStopAsync(Task previous, TaskCompletionSource done) {
		try {
			await previous.ConfigureAwait(false);
			var outcome = await _stop().ConfigureAwait(false);
			if (outcome.WasForced) {
				Log(SupervisorLogLevel.Warning, $"force-stopped: it {outcome.ForcedBecause}");
				lock (_gate) {
					QueueStoppedLocked(outcome);
				}
			}
		} catch (Exception ex) {
			Log(SupervisorLogLevel.Error, $"stop failed: {ex.Message}");
		} finally {
			done.SetResult();
		}
	}

	private bool OnInstanceEnded(SupervisedLaunch launch, int exitCode, bool stopCurrent) {
		SupervisorStateChanged change;
		TimeSpan? scheduleDelay = null;
		int scheduleAttempt = 0;
		CancellationToken token = default;
		bool cleanStop = false;
		bool tripped = false;
		TaskCompletionSource? stopDone = null;
		var stopPrevious = Task.CompletedTask;

		lock (_gate) {
			if (_disposal is not null || !ReferenceEquals(launch, _current) || _state != SupervisorState.Running) {
				return false; // intentional stop, a stopped predecessor's late exit, a duplicate, or already failed
			}

			if (!stopCurrent
				&& !_options.RequireExplicitHealth
				&& _clock.UtcNow - _startedAt >= _options.HealthyAfter) {
				_consecutiveCrashes = 0;
			}

			bool crash = exitCode != 0;
			bool restart = _options.Policy switch {
				RestartPolicy.Always => true,
				RestartPolicy.OnFailure => crash,
				_ => false,
			};

			if (!restart) {
				_state = SupervisorState.Idle;
				cleanStop = true;
				change = new SupervisorStateChanged(SupervisorState.Idle, exitCode, _restartCount);
			} else {
				PruneRestartWindow(_clock.UtcNow);
				if (_recentRestarts.Count >= _options.MaxRestartsInWindow
					|| _consecutiveCrashes >= _options.MaxConsecutiveFailures) {
					_state = SupervisorState.Failed;
					tripped = true;
					change = new SupervisorStateChanged(SupervisorState.Failed, exitCode, _restartCount);
				} else {
					_consecutiveCrashes++;
					_recentRestarts.Enqueue(_clock.UtcNow);
					scheduleDelay = ComputeBackoff(_consecutiveCrashes);
					scheduleAttempt = _attempt + 1;
					token = _cts!.Token;
					_state = SupervisorState.BackingOff;
					change = new SupervisorStateChanged(SupervisorState.BackingOff, exitCode, _restartCount);
				}
			}

			QueueStateChangedLocked(change);
			if (stopCurrent) {
				// The transition above makes a synchronous exit callback stale before the stop delegate runs, and
				// the replacement launch waits on this stop, so it never overlaps the instance being replaced.
				stopPrevious = BeginStopLocked(out var done);
				stopDone = done;
			}
		}

		if (stopDone is not null) {
			_ = RunStopAsync(stopPrevious, stopDone);
		}
		if (cleanStop) {
			Log(SupervisorLogLevel.Info, $"exited cleanly (code {exitCode}); not restarting");
		} else if (tripped) {
			Log(SupervisorLogLevel.Error, $"crashed too many times (code {exitCode}); giving up");
		} else if (scheduleDelay is TimeSpan delay) {
			Log(SupervisorLogLevel.Warning, $"exited (code {exitCode}); restarting in {delay.TotalMilliseconds:F0}ms");
			_ = RestartAfterDelayAsync(delay, scheduleAttempt, token);
		}

		return true;
	}

	private async Task RestartAfterDelayAsync(TimeSpan delay, int attempt, CancellationToken token) {
		try {
			await _clock.Delay(delay, token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			return; // stopped or disposed during backoff
		}

		SupervisedLaunch launch;
		Task stopping;
		lock (_gate) {
			if (_disposal is not null || token.IsCancellationRequested || _state != SupervisorState.BackingOff) {
				return;
			}

			_state = SupervisorState.Running;
			_attempt = attempt;
			_restartCount++;
			_startedAt = _clock.UtcNow;
			_generation++;
			launch = new SupervisedLaunch(this, attempt, _generation);
			_current = launch;
			stopping = _stopping;
			QueueStateChangedLocked(new SupervisorStateChanged(SupervisorState.Running, null, _restartCount));
		}

		Log(SupervisorLogLevel.Info, $"restarting (attempt {attempt})");
		LaunchAfter(stopping, launch);
	}

	private void LaunchAfter(Task stopping, SupervisedLaunch launch) {
		if (stopping.IsCompleted) {
			Launch(launch);
		} else {
			_ = LaunchWhenStoppedAsync(stopping, launch);
		}
	}

	private async Task LaunchWhenStoppedAsync(Task stopping, SupervisedLaunch launch) {
		await stopping.ConfigureAwait(false);
		if (IsCurrentLaunch(launch)) {
			Launch(launch);
		}
	}

	private void Launch(SupervisedLaunch launch) {
		try {
			_start(launch);
		} catch (Exception ex) {
			Log(SupervisorLogLevel.Warning, $"launch failed: {ex.Message}");
			OnInstanceEnded(launch, exitCode: -1, stopCurrent: false);
		}
	}

	internal bool IsCurrentLaunch(SupervisedLaunch launch) {
		lock (_gate) {
			return ReferenceEquals(_current, launch);
		}
	}

	private void PruneRestartWindow(DateTimeOffset now) {
		var cutoff = now - _options.CrashLoopWindow;
		while (_recentRestarts.Count > 0 && _recentRestarts.Peek() < cutoff) {
			_recentRestarts.Dequeue();
		}
	}

	private TimeSpan ComputeBackoff(int consecutiveCrashes) {
		double grown = _options.InitialBackoff.TotalMilliseconds * Math.Pow(_options.BackoffMultiplier, consecutiveCrashes - 1);
		double capped = Math.Min(grown, _options.MaxBackoff.TotalMilliseconds);
		return TimeSpan.FromMilliseconds(capped);
	}

	private void Log(SupervisorLogLevel level, string message) {
		var sink = _log;
		if (sink is null) {
			return;
		}

		var entry = new SupervisorLogEntry(Name, level, message);
		_logDiagnostics!.Run($"{Name} {level}: {message}", () => sink(entry));
	}
}
