namespace Weavie.Core.Processes;

/// <summary>
/// A <see cref="ProcessSupervisor"/> state transition; <see cref="ExitCode"/> is <see langword="null"/> when not
/// exit-driven (a launch or intentional stop), letting a UI tell a real exit apart from a deliberate teardown.
/// </summary>
/// <param name="State">The new state.</param>
/// <param name="ExitCode">The exit code behind the transition, or <see langword="null"/> if not exit-driven.</param>
/// <param name="RestartCount">How many restarts the supervisor has performed so far.</param>
public readonly record struct SupervisorStateChanged(SupervisorState State, int? ExitCode, int RestartCount);
