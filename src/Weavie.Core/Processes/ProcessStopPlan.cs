namespace Weavie.Core.Processes;

/// <summary>
/// How to ask one child to exit through its own protocol, and how long to wait before force-killing it. The
/// ask is protocol-specific on purpose: a .NET 10 child has no default SIGTERM handler, so only its own
/// shutdown path (stdin EOF, an LSP <c>shutdown</c>, an HTTP request) runs the runtime cleanup.
/// </summary>
public sealed record ProcessStopPlan {
	/// <summary>The ask in the user's words (e.g. <c>closing its input</c>).</summary>
	public required string Description { get; init; }

	/// <summary>Asks the child to exit; the token cancels once the deadline passes. An <see cref="IOException"/> means the child is already gone.</summary>
	public required Func<CancellationToken, Task> Ask { get; init; }

	/// <summary>How long the child gets to exit after the ask before it is force-killed.</summary>
	public required TimeSpan Deadline { get; init; }

	/// <summary>
	/// Closes the child's stdin — the EOF a stdio protocol server treats as the end of its session — once the write in
	/// flight under <paramref name="writeGate"/> finishes; a write the child never drains holds it to the deadline.
	/// </summary>
	public static ProcessStopPlan CloseInput(OwnedProcess process, SemaphoreSlim writeGate, TimeSpan deadline) {
		ArgumentNullException.ThrowIfNull(process);
		ArgumentNullException.ThrowIfNull(writeGate);
		return new ProcessStopPlan {
			Description = "closing its input",
			Ask = async ct => {
				await writeGate.WaitAsync(ct).ConfigureAwait(false);
				try {
					process.StandardInput.Close();
				} finally {
					writeGate.Release();
				}
			},
			Deadline = deadline,
		};
	}

	/// <summary>Asks through an owner-specific request, such as an HTTP shutdown endpoint.</summary>
	public static ProcessStopPlan Request(string description, Func<CancellationToken, Task> ask, TimeSpan deadline) {
		ArgumentException.ThrowIfNullOrEmpty(description);
		ArgumentNullException.ThrowIfNull(ask);
		return new ProcessStopPlan { Description = description, Ask = ask, Deadline = deadline };
	}
}
