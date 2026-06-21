namespace Weavie.Core.Git;

/// <summary>
/// Thrown when a git command that must succeed exits non-zero or git can't be started. Carries the
/// failing command and stderr so failures stay observable rather than papered over with a default.
/// </summary>
public sealed class GitException : Exception {
	/// <summary>Creates the exception with a failure <paramref name="message"/>.</summary>
	public GitException(string message) : base(message) {
	}

	/// <summary>Creates the exception wrapping an <paramref name="inner"/> cause.</summary>
	public GitException(string message, Exception inner) : base(message, inner) {
	}
}
