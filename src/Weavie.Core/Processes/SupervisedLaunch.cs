namespace Weavie.Core.Processes;

/// <summary>
/// One launched instance's handle, passed to the supervisor's <c>start</c> delegate. Reporting the exit through
/// the handle attributes it to this specific instance, so a stopped predecessor's late exit can never be mistaken
/// for the current instance's crash (and restart a healthy replacement).
/// </summary>
public sealed class SupervisedLaunch {
	private readonly ProcessSupervisor _supervisor;

	internal SupervisedLaunch(ProcessSupervisor supervisor, int attempt) {
		_supervisor = supervisor;
		Attempt = attempt;
	}

	/// <summary>The launch attempt (0 = the first launch since <see cref="ProcessSupervisor.Start"/>).</summary>
	public int Attempt { get; }

	/// <summary>
	/// Reports that this instance has exited with <paramref name="exitCode"/>. May be called from any thread;
	/// ignored once this instance is no longer the supervisor's current one.
	/// </summary>
	/// <param name="exitCode">The process exit code (0 = clean).</param>
	public void NotifyExited(int exitCode) => _supervisor.NotifyLaunchExited(this, exitCode);
}
