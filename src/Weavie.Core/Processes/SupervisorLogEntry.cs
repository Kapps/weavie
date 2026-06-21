namespace Weavie.Core.Processes;

/// <summary>Severity of a <see cref="SupervisorLogEntry"/>.</summary>
public enum SupervisorLogLevel {
	/// <summary>Normal lifecycle progress (start, clean exit, scheduled restart).</summary>
	Info,

	/// <summary>A crash that is being recovered from (a restart was scheduled).</summary>
	Warning,

	/// <summary>The supervisor gave up — the crash-loop breaker tripped, or a stop/launch delegate threw.</summary>
	Error,
}

/// <summary>
/// One line of structured lifecycle logging from a <see cref="ProcessSupervisor"/>; the host decides where it
/// goes (see <c>docs/specs/process-supervisor.md</c>).
/// </summary>
/// <param name="Name">The supervised process's name.</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">Human-readable description of what happened.</param>
public readonly record struct SupervisorLogEntry(string Name, SupervisorLogLevel Level, string Message);
