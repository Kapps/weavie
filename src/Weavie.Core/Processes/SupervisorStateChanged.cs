namespace Weavie.Core.Processes;

/// <summary>
/// A <see cref="ProcessSupervisor"/> state transition. <see cref="ExitCode"/> is the process exit code that
/// drove the transition (an exit or crash), or <see langword="null"/> when the transition was not driven by a
/// process exit (a launch, or an intentional stop). That distinction lets a UI tell a real exit apart from a
/// deliberate teardown — e.g. show a "process exited" notice only when <see cref="ExitCode"/> is set.
/// </summary>
/// <param name="State">The new state.</param>
/// <param name="ExitCode">The exit code behind the transition, or <see langword="null"/> if not exit-driven.</param>
/// <param name="RestartCount">How many restarts the supervisor has performed so far.</param>
public readonly record struct SupervisorStateChanged(SupervisorState State, int? ExitCode, int RestartCount);
