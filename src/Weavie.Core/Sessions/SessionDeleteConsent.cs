namespace Weavie.Core.Sessions;

/// <summary>What the caller of a session delete agreed to, so the host refuses a delete that would do more.</summary>
public enum SessionDeleteConsent {
	/// <summary>A clean worktree is removed; a delete that would lose work is refused.</summary>
	Clean,

	/// <summary>Uncommitted changes and commits no branch keeps may go with the worktree.</summary>
	LoseWork,

	/// <summary>Only the session goes; a delete that would remove its folder is refused.</summary>
	KeepFolder,
}
