namespace Weavie.Core.Review;

/// <summary>One review conversation: its root comment and every reply, anchored where the root is.</summary>
/// <param name="RootId">The root comment's id — the reply target.</param>
/// <param name="Path">The repository-relative file path.</param>
/// <param name="Line">The root's 1-based line on <paramref name="Side"/>; 0 when <paramref name="Outdated"/>.</param>
/// <param name="Side"><c>"right"</c> (head) or <c>"left"</c> (merge-base).</param>
/// <param name="Outdated">True when the anchor no longer exists in the PR's current diff.</param>
/// <param name="Comments">The root first, then its replies in creation order.</param>
public sealed record ReviewThread(
	long RootId,
	string Path,
	int Line,
	string Side,
	bool Outdated,
	IReadOnlyList<ReviewComment> Comments) {
	/// <summary>Groups flat forge comments into threads by root, in root creation order. Pure.</summary>
	public static IReadOnlyList<ReviewThread> Group(IReadOnlyList<ReviewComment> comments) {
		ArgumentNullException.ThrowIfNull(comments);
		var byId = comments.ToDictionary(c => c.Id);
		return [.. comments
			.OrderBy(c => c.CreatedAt, StringComparer.Ordinal).ThenBy(c => c.Id)
			.GroupBy(c => RootOf(c, byId).Id)
			.Select(g => {
				var root = byId[g.Key];
				return new ReviewThread(root.Id, root.Path, root.Line, root.Side, root.Outdated,
					[root, .. g.Where(c => c.Id != root.Id)]);
			})];
	}

	// A reply whose parent is gone roots its own thread rather than vanishing.
	private static ReviewComment RootOf(ReviewComment comment, Dictionary<long, ReviewComment> byId) {
		while (comment.InReplyTo != 0 && byId.TryGetValue(comment.InReplyTo, out var parent)) {
			comment = parent;
		}

		return comment;
	}
}
