using System.Collections.Concurrent;
using Weavie.Core.Processes;

namespace Weavie.Core.Git;

// One operation asks git the same question from several places; an operation's own instance runs each read once.
public sealed partial class GitService {
	private readonly ConcurrentDictionary<string, Task<ProcessCaptureResult>>? _reads;

	/// <summary>Creates a service that runs git for every call.</summary>
	public GitService() { }

	private GitService(ConcurrentDictionary<string, Task<ProcessCaptureResult>> reads) {
		_reads = reads;
	}

	/// <summary>
	/// A service for one operation: its branch, ref, config and merge-base reads each run git once, so the parts of an
	/// operation that ask the same question share one answer. Never keep one past the operation it was made for.
	/// </summary>
	public static GitService ForOperation() => new(new ConcurrentDictionary<string, Task<ProcessCaptureResult>>(StringComparer.Ordinal));

	// A read whose answer only changes when the repository does.
	private Task<ProcessCaptureResult> ReadAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) =>
		_reads is null
			? RunAsync(workingDirectory, args, ct)
			: _reads.GetOrAdd($"{workingDirectory}\0{string.Join('\0', args)}", _ => RunAsync(workingDirectory, args, ct));

	private async Task<ProcessCaptureResult> ReadCheckedAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) =>
		Checked(await ReadAsync(workingDirectory, args, ct).ConfigureAwait(false), args);
}
