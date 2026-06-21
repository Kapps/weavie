namespace Weavie.Core.Changes;

/// <summary>
/// A 1-based, end-exclusive line range, matching VSCode's <c>LineRange</c> (the web's per-hunk
/// <c>originalRange</c> / <c>modifiedRange</c>). A document of N lines spans valid ranges within <c>[1, N+1)</c>.
/// </summary>
/// <param name="Start">The 1-based first line of the range.</param>
/// <param name="EndExclusive">The 1-based line just past the range's end.</param>
public readonly record struct LineRange(int Start, int EndExclusive);

/// <summary>The result of <see cref="SessionChangeTracker.RevertHunk"/>.</summary>
public enum RevertHunkOutcome {
	/// <summary>The current lines didn't match the guard text — nothing was written (a concurrent edit moved the file).</summary>
	GuardMismatch,

	/// <summary>The hunk was reverted; the file was rewritten on disk with the baseline lines spliced back in.</summary>
	Reverted,

	/// <summary>Reverting returned a created-since-baseline file to non-existence — the file was deleted.</summary>
	Deleted,
}
