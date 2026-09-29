using Weavie.Core.Changes;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private ReviewDecisionNavigation RunReviewDecision(HostSession session, Func<ReviewDecisionNavigation> apply) {
		try {
			return apply();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify(session, "warn", $"Couldn't apply your review decision: {ex.Message}");
			return ReviewDecisionNavigation.None;
		}
	}
}
