namespace Weavie.Core.Processes;

/// <summary>
/// Thrown when a captured run's token fires: the child's process tree is killed and whatever it managed to print
/// is carried here, so a caller that bound the run can tell the user what it was doing when it stopped.
/// </summary>
public sealed class ProcessCanceledException : OperationCanceledException {
	/// <summary>Creates the exception carrying the output captured before the child was killed.</summary>
	public ProcessCanceledException(string stdOut, string stdErr, CancellationToken token)
		: base("The captured process was canceled and its process tree killed.", token) {
		StdOut = stdOut;
		StdErr = stdErr;
	}

	/// <summary>Everything the child wrote to stdout before it was killed.</summary>
	public string StdOut { get; }

	/// <summary>Everything the child wrote to stderr before it was killed.</summary>
	public string StdErr { get; }
}
