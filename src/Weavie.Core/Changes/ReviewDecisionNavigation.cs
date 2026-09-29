using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

/// <summary>The next pending location authorized by a completed review decision.</summary>
/// <param name="Path">Absolute file path.</param>
/// <param name="Line">First pending line, or the existence-change anchor.</param>
public sealed record ReviewDecisionLocation(string Path, int Line);

/// <summary>Navigation captured atomically with a review mutation, independently of its later projections.</summary>
/// <param name="SourceDeleted">Whether this exact decision deleted its source file.</param>
/// <param name="SourceHasReview">Whether the source still differs from its accepted anchor, including kept changes.</param>
/// <param name="Next">The next pending location, or null when this decision does not advance.</param>
public sealed record ReviewDecisionNavigation(bool SourceDeleted, bool SourceHasReview, ReviewDecisionLocation? Next) {
	/// <summary>No permission to advance or retire a source presentation.</summary>
	public static ReviewDecisionNavigation None { get; } = new(false, false, null);
}

public sealed partial class SessionChangeTracker {
	private ReviewDecisionNavigation DecisionNavigationLocked(string path, string[] order, bool sourceDeleted) {
		var source = GetTurn(path);
		bool sourceHasReview = source is not null && (source.AcceptedBaselineText != source.CurrentText
			|| source.AcceptedBaselineExists != source.CurrentExists);
		if (source is not null && (source.BaselineText != source.CurrentText || source.BaselineExists != source.CurrentExists)) {
			return new(sourceDeleted, sourceHasReview, null);
		}

		int index = Array.FindIndex(order, candidate => PathIdentity.Comparer.Equals(candidate, path));
		if (index >= 0) {
			var pending = TurnChangeSummaries()
				.Where(summary => summary.Change.BaselineText != summary.Change.CurrentText
					|| summary.Change.BaselineExists != summary.Change.CurrentExists)
				.ToDictionary(summary => summary.Change.Path, PathIdentity.Comparer);
			for (int step = 1; step < order.Length; step++) {
				if (pending.TryGetValue(order[(index + step) % order.Length], out var next)) {
					return new(sourceDeleted, sourceHasReview, new(next.Change.Path, next.Line));
				}
			}
		}
		return new(sourceDeleted, sourceHasReview, null);
	}
}
