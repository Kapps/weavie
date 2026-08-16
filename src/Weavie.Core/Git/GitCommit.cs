namespace Weavie.Core.Git;

/// <summary>One commit as a history list needs it — who changed a line or a file, when, and why.</summary>
/// <param name="Sha">The full commit sha.</param>
/// <param name="Author">The author's name as recorded on the commit.</param>
/// <param name="TimeUnix">The author time as seconds since the Unix epoch.</param>
/// <param name="Summary">The commit's subject line.</param>
public sealed record GitCommit(string Sha, string Author, long TimeUnix, string Summary);

/// <summary>
/// A commit from a line-scoped log, paired with where the tracked line sat in it. Git follows the line back
/// through each rewrite, so <see cref="Line"/> is the anchor for pulling that commit's hunk around it.
/// </summary>
/// <param name="Commit">The commit itself.</param>
/// <param name="Line">The tracked line's number in this commit's version of the file; 0 when it only removed it.</param>
public sealed record GitLineCommit(GitCommit Commit, int Line);
