namespace Weavie.Core.Processes;

/// <summary>
/// The outcome of one captured run, with both pipes fully drained. A non-zero exit code and a start failure are
/// answers, not errors: each caller decides which of them is fatal for what it was asking.
/// </summary>
/// <param name="ExitCode">The child's exit code, or -1 when it never started.</param>
/// <param name="StdOutBytes">Everything the child wrote to stdout, as raw bytes.</param>
/// <param name="StdErr">Everything the child wrote to stderr.</param>
/// <param name="StartFailure">Why the child could not be started at all, else null — the one outcome no exit code can express.</param>
public readonly record struct ProcessCaptureResult(
	int ExitCode,
	byte[] StdOutBytes,
	string StdErr,
	Exception? StartFailure) {
	/// <summary>Everything the child wrote to stdout, decoded as UTF-8 (a leading byte-order mark selects its encoding).</summary>
	public string StdOut { get; } = ProcessOutput.Decode(StdOutBytes);
}
