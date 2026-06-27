namespace Weavie.Core.Review;

/// <summary>
/// One review comment anchored to a line of a PR's diff. Forge-neutral. <see cref="Side"/> is <c>"right"</c>
/// (the head/added side — the common case) or <c>"left"</c> (the base side); <see cref="InReplyTo"/> is the id
/// of the comment this replies to, or 0 for a top-level thread.
/// </summary>
public sealed record ReviewComment {
	/// <summary>The comment's id (unique within the repo); the reply target.</summary>
	public required long Id { get; init; }

	/// <summary>The repository-relative file path the comment is on.</summary>
	public required string Path { get; init; }

	/// <summary>The 1-based line in the file the comment anchors to (on <see cref="Side"/>).</summary>
	public required int Line { get; init; }

	/// <summary>Which side of the diff the line is on: <c>"right"</c> (head) or <c>"left"</c> (base).</summary>
	public required string Side { get; init; }

	/// <summary>The comment author's login.</summary>
	public required string Author { get; init; }

	/// <summary>The comment body (markdown; sanitized before render).</summary>
	public required string Body { get; init; }

	/// <summary>ISO-8601 creation timestamp, as the forge returns it.</summary>
	public required string CreatedAt { get; init; }

	/// <summary>The id of the comment this replies to, or 0 for a top-level comment.</summary>
	public required long InReplyTo { get; init; }
}

/// <summary>A new top-level review comment to post on a PR (the host supplies the commit id).</summary>
public sealed record NewReviewComment {
	/// <summary>The repository-relative file path to comment on.</summary>
	public required string Path { get; init; }

	/// <summary>The 1-based line to anchor the comment to.</summary>
	public required int Line { get; init; }

	/// <summary>Which side of the diff: <c>"right"</c> (head) or <c>"left"</c> (base).</summary>
	public required string Side { get; init; }

	/// <summary>The comment body.</summary>
	public required string Body { get; init; }
}
