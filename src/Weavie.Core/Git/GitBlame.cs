namespace Weavie.Core.Git;

/// <summary>
/// One commit a blame attributes lines to. <see cref="Uncommitted"/> marks Git's all-zero sha — a line that
/// only exists in the working tree.
/// </summary>
/// <param name="Sha">The full commit sha, or forty zeroes for a working-tree line.</param>
/// <param name="Author">The author's name as recorded on the commit.</param>
/// <param name="AuthorEmail">The author's email, angle brackets stripped.</param>
/// <param name="TimeUnix">The author time as seconds since the Unix epoch.</param>
/// <param name="Summary">The commit's subject line.</param>
/// <param name="Uncommitted">True when the lines are working-tree-only, not yet in any commit.</param>
public sealed record BlameCommit(
	string Sha,
	string Author,
	string AuthorEmail,
	long TimeUnix,
	string Summary,
	bool Uncommitted);

/// <summary>
/// A file's blame, deduplicated: <see cref="Commits"/> holds each attributed commit once and the per-line arrays
/// index into it, so a long file's blame stays small on the wire.
/// </summary>
/// <param name="Commits">Every commit the file's lines are attributed to, in first-seen order.</param>
/// <param name="LineCommits">Per line (index 0 = line 1), the index into <see cref="Commits"/>.</param>
/// <param name="LineOriginalLines">
/// Per line, that line's number inside its attributed commit — the anchor for pulling the hunk that introduced it.
/// </param>
public sealed record GitBlame(
	IReadOnlyList<BlameCommit> Commits,
	IReadOnlyList<int> LineCommits,
	IReadOnlyList<int> LineOriginalLines) {
	/// <summary>A blame with no lines — an empty or unblameable file.</summary>
	public static GitBlame Empty { get; } = new([], [], []);
}
