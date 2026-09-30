namespace Weavie.Core.Review;

/// <summary>The pull request a session's branch belongs to, as the forge last reported it.</summary>
/// <param name="Repo">The repository the PR lives in.</param>
/// <param name="Remote">The git remote that hosts <paramref name="Repo"/> (fetches the PR head and base).</param>
/// <param name="Number">The PR number.</param>
/// <param name="HeadSha">The forge's current PR head commit.</param>
/// <param name="BaseRef">The branch the PR targets.</param>
/// <param name="Url">The PR's web URL.</param>
public sealed record PullRequestTarget(RepoRef Repo, string Remote, int Number, string HeadSha, string BaseRef, string Url);

/// <summary>One PR's loaded review comments plus what a new comment may anchor to.</summary>
/// <param name="Number">The PR number.</param>
/// <param name="Url">The PR's web URL.</param>
/// <param name="HeadSha">The PR head every post is made against.</param>
/// <param name="Viewer">The authenticated forge login.</param>
/// <param name="ChangedPaths">Repository-relative paths changed between the merge-base and <paramref name="HeadSha"/>.</param>
/// <param name="Threads">The PR's review threads.</param>
public sealed record PullRequestCommentSet(
	int Number,
	string Url,
	string HeadSha,
	string Viewer,
	IReadOnlyList<string> ChangedPaths,
	IReadOnlyList<ReviewThread> Threads) {
	/// <summary>Whether <paramref name="comment"/> was written by <see cref="Viewer"/>.</summary>
	public bool IsMine(ReviewComment comment) {
		ArgumentNullException.ThrowIfNull(comment);
		return string.Equals(comment.Author, Viewer, StringComparison.OrdinalIgnoreCase);
	}
}

/// <summary>A session's PR comments: the last good set (null without a PR) and the latest load failure.</summary>
/// <param name="Set">The loaded comments, or null when the branch has no PR.</param>
/// <param name="Error">Why the latest load failed, or null.</param>
public sealed record PullRequestCommentsSnapshot(PullRequestCommentSet? Set, string? Error);

/// <summary>A file's text at the PR head and at its merge-base; null where the file is absent.</summary>
/// <param name="Head">The file at the PR head.</param>
/// <param name="Base">The file at the merge-base.</param>
public sealed record PullRequestSources(string? Head, string? Base);
