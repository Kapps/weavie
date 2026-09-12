namespace Weavie.Core.Git;

/// <summary>
/// Thrown when a git command that must succeed exits non-zero or git can't be started. Carries the
/// failing command and stderr so failures stay observable rather than papered over with a default.
/// </summary>
public sealed class GitException : Exception {
	/// <summary>
	/// The fixed wording every "this working directory no longer exists" failure carries. A caller deciding
	/// whether a workspace vanished can recognize it this way instead of re-deriving the same fact with its own
	/// filesystem check — one that would race the very deletion in progress and can disagree with what git,
	/// moments earlier, already found.
	/// </summary>
	public const string WorkingDirectoryMissingPrefix = "Git working directory does not exist: ";

	/// <summary>Creates the exception with a failure <paramref name="message"/>.</summary>
	public GitException(string message) : base(message) {
	}

	/// <summary>Creates the exception wrapping an <paramref name="inner"/> cause.</summary>
	public GitException(string message, Exception inner) : base(message, inner) {
	}

	/// <summary>Creates the exception for a working directory that no longer exists.</summary>
	public static GitException ForMissingWorkingDirectory(string directory) =>
		new(WorkingDirectoryMissingPrefix + directory);
}
