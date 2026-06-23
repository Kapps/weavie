namespace Weavie.Core.Processes;

/// <summary>The lifecycle state of a <see cref="ProcessSupervisor"/>.</summary>
public enum SupervisorState {
	/// <summary>
	/// Not running and not scheduled to start: never started, intentionally stopped, or exited under a policy
	/// that declined to relaunch.
	/// </summary>
	Idle,

	/// <summary>The process is running.</summary>
	Running,

	/// <summary>The process exited and a relaunch is scheduled after the backoff delay.</summary>
	BackingOff,

	/// <summary>
	/// The process restarted too many times within the crash-loop window; the supervisor has given up and will
	/// not relaunch it until told to <see cref="ProcessSupervisor.Start"/> again.
	/// </summary>
	Failed,
}
