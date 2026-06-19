namespace Weavie.Core.Git;

/// <summary>
/// Thrown when a git invocation fails unexpectedly — a non-zero exit from a command that was expected
/// to succeed, or git not being available at all. Carries the failing command and git's stderr so the
/// failure stays observable; Weavie never papers over a failed git operation with a silent default.
/// </summary>
public sealed class GitException : Exception {
	/// <summary>Creates a <see cref="GitException"/> with a human-readable <paramref name="message"/>.</summary>
	public GitException(string message) : base(message) {
	}

	/// <summary>Creates a <see cref="GitException"/> wrapping an underlying <paramref name="inner"/> failure.</summary>
	public GitException(string message, Exception inner) : base(message, inner) {
	}
}
