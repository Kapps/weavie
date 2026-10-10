using Weavie.Core.Review.Hints;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The session's agent review hints on the `reviewHints` feature. Core speaks worktree-relative paths; the wire
// speaks absolute ones.
public sealed partial class HostSession {
	/// <summary>This session's agent review findings, shown inline in every editor.</summary>
	public ReviewHintStore ReviewHints { get; }

	private void PublishReviewHints(IReadOnlyList<ReviewHint> hints) =>
		State.Set("reviewHints", "hints", "hints", WireJson.Default.ReviewHintsWire, new([.. hints.Select(hint => new ReviewHintWire(
			hint.Id,
			Path.GetFullPath(Path.Combine(WorkspaceRoot, hint.Path)),
			hint.Line,
			ReviewHintSeverities.Name(hint.Severity),
			hint.Title,
			hint.Body,
			hint.Category,
			hint.Agent,
			hint.SourceHash,
			hint.Quote))]));
}

internal sealed record ReviewHintsWire(IReadOnlyList<ReviewHintWire> Hints);

internal sealed record ReviewHintWire(
	long Id,
	string Path,
	int Line,
	string Severity,
	string Title,
	string Body,
	string? Category,
	string Agent,
	string SourceHash,
	string Quote);
