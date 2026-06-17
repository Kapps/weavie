namespace Weavie.Core.Processes;

/// <summary>
/// Tunables for a <see cref="ProcessSupervisor"/>: the restart policy plus the exponential-backoff and
/// crash-loop-breaker parameters. The defaults suit a long-lived child process (claude, a shell, a language
/// server): a half-second initial backoff doubling to a 30-second ceiling, a run resets the backoff once it
/// has lasted ten seconds, and more than five restarts in a minute trips the breaker.
/// </summary>
public sealed record SupervisionOptions {
	/// <summary>How exits are handled. Required.</summary>
	public required RestartPolicy Policy { get; init; }

	/// <summary>Backoff before the first restart; grows by <see cref="BackoffMultiplier"/> each consecutive crash.</summary>
	public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(500);

	/// <summary>Ceiling on the backoff delay, however many consecutive crashes have occurred.</summary>
	public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>Factor the backoff grows by after each consecutive crash. Must be &gt;= 1.</summary>
	public double BackoffMultiplier { get; init; } = 2.0;

	/// <summary>
	/// A run lasting at least this long is treated as healthy: the consecutive-crash count resets, so the
	/// next crash restarts at <see cref="InitialBackoff"/> rather than the grown delay.
	/// </summary>
	public TimeSpan HealthyAfter { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>The sliding window over which restarts are counted for the crash-loop breaker.</summary>
	public TimeSpan CrashLoopWindow { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// The most restarts permitted within <see cref="CrashLoopWindow"/>; the next crash beyond it trips the
	/// breaker (state <see cref="SupervisorState.Failed"/>) instead of hot-looping a broken binary. Must be &gt;= 0.
	/// </summary>
	public int MaxRestartsInWindow { get; init; } = 5;
}
