namespace Weavie.Core.Processes;

/// <summary>
/// The clock a <see cref="ProcessSupervisor"/> reads time and schedules backoff delays through, so tests can
/// drive timing deterministically without real waiting.
/// </summary>
public interface ISupervisorClock {
	/// <summary>The current UTC time.</summary>
	DateTimeOffset UtcNow { get; }

	/// <summary>Completes after <paramref name="delay"/>, or cancels when <paramref name="cancellationToken"/> fires.</summary>
	/// <param name="delay">How long to wait.</param>
	/// <param name="cancellationToken">Cancels the wait.</param>
	Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The real clock: wall-clock time and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemSupervisorClock : ISupervisorClock {
	/// <inheritdoc/>
	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	/// <inheritdoc/>
	public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
