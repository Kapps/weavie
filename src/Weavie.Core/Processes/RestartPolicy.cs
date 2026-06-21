namespace Weavie.Core.Processes;

/// <summary>Whether <see cref="ProcessSupervisor"/> relaunches a supervised process when it exits.</summary>
public enum RestartPolicy {
	/// <summary>Launch once; never relaunch, whatever the exit code.</summary>
	Never,

	/// <summary>Relaunch only after a crash (a non-zero exit); a clean exit (code 0) is left stopped.</summary>
	OnFailure,

	/// <summary>
	/// Relaunch after every exit, clean or crash — for a permanent UI fixture (a terminal pane) where a
	/// stopped, empty panel is never the desired end state.
	/// </summary>
	Always,
}
