namespace Weavie.Core.Processes;

/// <summary>
/// Supervises a single long-lived child: launches it via a caller-supplied <c>start</c> delegate, watches for
/// exit, and relaunches it per a <see cref="RestartPolicy"/> with exponential backoff and a crash-loop breaker.
/// Process-agnostic — a PTY terminal, a <see cref="System.Diagnostics.Process"/>, or anything else is supervised
/// the same way.
/// <para>Thread-safe. <see cref="SupervisedLaunch.NotifyExited"/> may be called from any thread; an exit
/// arriving while not <see cref="SupervisorState.Running"/> is ignored, as is any exit reported for an instance
/// that is no longer the current one (so a stopped predecessor's late exit never restarts its healthy replacement).
/// <see cref="StateChanged"/> handlers run off the internal lock and must not block.</para>
/// </summary>
public sealed class ProcessSupervisor : IDisposable {
	private readonly Action<SupervisedLaunch> _start;
	private readonly Action _stop;
	private readonly SupervisionOptions _options;
	private readonly Action<SupervisorLogEntry>? _log;
	private readonly ISupervisorClock _clock;
	private readonly object _gate = new();
	private readonly Queue<DateTimeOffset> _recentRestarts = new();

	private SupervisorState _state = SupervisorState.Idle;
	private int _attempt;              // launches so far (0 = first launch in flight, grows on restart)
	private SupervisedLaunch? _current; // the live instance's handle; exits from any other handle are stale
	private int _restartCount;         // launches beyond the first
	private int _consecutiveCrashes;   // drives backoff growth; reset by a healthy run
	private DateTimeOffset _startedAt;
	private CancellationTokenSource? _cts;
	private bool _disposed;

	/// <summary>Creates a supervisor. Nothing launches until <see cref="Start"/> is called.</summary>
	/// <param name="name">A short name for logging and status (e.g. <c>"terminal:claude"</c>).</param>
	/// <param name="start">
	/// Launches a fresh instance and wires its exit to the handle's <see cref="SupervisedLaunch.NotifyExited"/>.
	/// An exception thrown here is treated as a crash and feeds the backoff/breaker.
	/// </param>
	/// <param name="stop">
	/// Kills/disposes the current instance; must be a safe no-op when nothing is running.
	/// </param>
	/// <param name="options">Restart policy and backoff/crash-loop tunables.</param>
	/// <param name="log">Optional sink for structured lifecycle logging.</param>
	/// <param name="clock">Clock for delays and timing; defaults to <see cref="SystemSupervisorClock"/>.</param>
	public ProcessSupervisor(
		string name,
		Action<SupervisedLaunch> start,
		Action stop,
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

		Name = name;
		_start = start;
		_stop = stop;
		_options = options;
		_log = log;
		_clock = clock ?? new SystemSupervisorClock();
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

	/// <summary>Raised after every state transition, off the lock. Handlers must not block.</summary>
	public event Action<SupervisorStateChanged>? StateChanged;

	/// <summary>
	/// Launches the process (attempt 0). No-op if it is already running or scheduled to restart, or if the
	/// supervisor has been disposed.
	/// </summary>
	public void Start() {
		SupervisorStateChanged change;
		lock (_gate) {
			if (_disposed || _state is SupervisorState.Running or SupervisorState.BackingOff) {
				return;
			}

			_cts?.Dispose();
			_cts = new CancellationTokenSource();
			_state = SupervisorState.Running;
			_attempt = 0;
			_startedAt = _clock.UtcNow;
			change = new SupervisorStateChanged(SupervisorState.Running, null, _restartCount);
		}

		Raise(change);
		Log(SupervisorLogLevel.Info, "starting");
		Launch(0);
	}

	internal void NotifyLaunchExited(SupervisedLaunch launch, int exitCode) => OnInstanceEnded(launch, exitCode);

	/// <summary>
	/// Stops the process and cancels any pending restart; the resulting exit is treated as intentional and is
	/// not relaunched. No-op if already idle or disposed. Safe to call repeatedly.
	/// </summary>
	public void Stop() {
		SupervisorStateChanged change;
		lock (_gate) {
			if (_disposed || _state == SupervisorState.Idle) {
				return;
			}

			_cts?.Cancel();
			_state = SupervisorState.Idle;
			_consecutiveCrashes = 0;
			_recentRestarts.Clear();
			change = new SupervisorStateChanged(SupervisorState.Idle, null, _restartCount);
		}

		Raise(change);
		Log(SupervisorLogLevel.Info, "stopping");
		SafeStop();
	}

	/// <summary>Stops the process (no relaunch) and releases resources. Idempotent.</summary>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			_cts?.Cancel();
			_state = SupervisorState.Idle;
		}

		SafeStop();
		_cts?.Dispose();
	}

	private void OnInstanceEnded(SupervisedLaunch launch, int exitCode) {
		SupervisorStateChanged change;
		TimeSpan? scheduleDelay = null;
		int scheduleAttempt = 0;
		CancellationToken token = default;
		bool cleanStop = false;
		bool tripped = false;

		lock (_gate) {
			if (_disposed || !ReferenceEquals(launch, _current) || _state != SupervisorState.Running) {
				return; // intentional stop, a stopped predecessor's late exit, a duplicate, or already failed
			}

			if (_clock.UtcNow - _startedAt >= _options.HealthyAfter) {
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
				if (_recentRestarts.Count >= _options.MaxRestartsInWindow) {
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
		}

		Raise(change);
		if (cleanStop) {
			Log(SupervisorLogLevel.Info, $"exited cleanly (code {exitCode}); not restarting");
		} else if (tripped) {
			Log(SupervisorLogLevel.Error, $"crashed too many times (code {exitCode}); giving up");
		} else if (scheduleDelay is TimeSpan delay) {
			Log(SupervisorLogLevel.Warning, $"exited (code {exitCode}); restarting in {delay.TotalMilliseconds:F0}ms");
			_ = RestartAfterDelayAsync(delay, scheduleAttempt, token);
		}
	}

	private async Task RestartAfterDelayAsync(TimeSpan delay, int attempt, CancellationToken token) {
		try {
			await _clock.Delay(delay, token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			return; // stopped or disposed during backoff
		}

		SupervisorStateChanged change;
		lock (_gate) {
			if (_disposed || token.IsCancellationRequested || _state != SupervisorState.BackingOff) {
				return;
			}

			_state = SupervisorState.Running;
			_attempt = attempt;
			_restartCount++;
			_startedAt = _clock.UtcNow;
			change = new SupervisorStateChanged(SupervisorState.Running, null, _restartCount);
		}

		Raise(change);
		Log(SupervisorLogLevel.Info, $"restarting (attempt {attempt})");
		Launch(attempt);
	}

	private void Launch(int attempt) {
		var launch = new SupervisedLaunch(this, attempt);
		lock (_gate) {
			_current = launch;
		}

		try {
			_start(launch);
		} catch (Exception ex) {
			Log(SupervisorLogLevel.Warning, $"launch failed: {ex.Message}");
			OnInstanceEnded(launch, exitCode: -1);
		}
	}

	private void SafeStop() {
		try {
			_stop();
		} catch (Exception ex) {
			Log(SupervisorLogLevel.Error, $"stop failed: {ex.Message}");
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

	private void Raise(SupervisorStateChanged change) => StateChanged?.Invoke(change);

	private void Log(SupervisorLogLevel level, string message) =>
		_log?.Invoke(new SupervisorLogEntry(Name, level, message));
}
