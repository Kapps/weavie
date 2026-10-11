namespace Weavie.Core.Review;

/// <summary>One comment in a PR review thread. Forge-neutral; the thread owns where it anchors.</summary>
public sealed record ReviewComment {
	/// <summary>The forge's opaque id for the comment: the edit and discard target.</summary>
	public required string Id { get; init; }

	/// <summary>The comment author's login.</summary>
	public required string Author { get; init; }

	/// <summary>The author's avatar image URL as the forge serves it, or empty when it has none.</summary>
	public required string AuthorAvatarUrl { get; init; }

	/// <summary>The comment's web page on the forge, or empty when it has none.</summary>
	public required string Url { get; init; }

	/// <summary>The comment body (markdown; sanitized before render).</summary>
	public required string Body { get; init; }

	/// <summary>ISO-8601 creation timestamp, as the forge returns it.</summary>
	public required string CreatedAt { get; init; }

	/// <summary>ISO-8601 last-edit timestamp, as the forge returns it.</summary>
	public required string UpdatedAt { get; init; }

	/// <summary>True for a draft in the viewer's pending review, visible only to them until it's submitted.</summary>
	public required bool Pending { get; init; }

	/// <summary>True when the authenticated viewer wrote the comment.</summary>
	public required bool Mine { get; init; }
}

/// <summary>A new top-level review comment on a PR-head line.</summary>
public sealed record NewReviewComment {
	/// <summary>The repository-relative file path to comment on.</summary>
	public required string Path { get; init; }

	/// <summary>The 1-based PR-head line to anchor the comment to.</summary>
	public required int Line { get; init; }

	/// <summary>The comment body.</summary>
	public required string Body { get; init; }
}
