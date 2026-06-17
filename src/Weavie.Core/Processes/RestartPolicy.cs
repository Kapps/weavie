namespace Weavie.Core.Processes;

/// <summary>
/// When a supervised process exits, whether <see cref="ProcessSupervisor"/> relaunches it.
/// </summary>
public enum RestartPolicy {
	/// <summary>Launch once; never relaunch, whatever the exit code.</summary>
	Never,

	/// <summary>Relaunch only after a crash (a non-zero exit); a clean exit (code 0) is left stopped.</summary>
	OnFailure,

	/// <summary>
	/// Relaunch after every exit, clean or crash — for a process that is a permanent fixture of the UI
	/// (a terminal pane), where a stopped, empty panel is never the desired end state.
	/// </summary>
	Always,
}
