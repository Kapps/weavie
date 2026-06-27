namespace Weavie.Core.Review;

/// <summary>
/// One open pull request, as the picker needs it and the open-PR flow checks it out. Forge-neutral: a GitLab
/// merge request or a Bitbucket PR would map onto the same shape.
/// </summary>
public sealed record PullRequestSummary {
	/// <summary>The PR number (<c>#123</c>), unique within the repo.</summary>
	public required int Number { get; init; }

	/// <summary>The PR title.</summary>
	public required string Title { get; init; }

	/// <summary>The login of the PR's author.</summary>
	public required string Author { get; init; }

	/// <summary>The head branch the PR is built from — the branch a session checks out.</summary>
	public required string HeadRef { get; init; }

	/// <summary>The base branch the PR targets (e.g. <c>main</c>) — the other side of the diff.</summary>
	public required string BaseRef { get; init; }

	/// <summary>The PR's web URL (the overview source resolves this; the seed prompt cites it).</summary>
	public required string Url { get; init; }

	/// <summary>True for a draft PR, so the picker can mark it.</summary>
	public required bool IsDraft { get; init; }
}
