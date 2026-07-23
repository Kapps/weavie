namespace Weavie.Core.Changes;

internal static class ReviewCheckpointValidator {
	public static void Validate(ReviewCheckpoint checkpoint) {
		if (checkpoint.Files is null
			|| checkpoint.Guards is null
			|| checkpoint.ArmToken < 0
			|| checkpoint.ActiveReviewToken < 0
			|| checkpoint.NextOriginId < 0) {
			throw Invalid();
		}
		if (checkpoint.Review is { } review
			&& (review.PrNumber < 0
				|| string.IsNullOrEmpty(review.Label)
				|| review.HeadRef is null
				|| string.IsNullOrEmpty(review.MergeBase)
				|| string.IsNullOrEmpty(review.HeadSha)
				|| string.IsNullOrEmpty(review.Worktree)
				|| review.Repo is { } repo
					&& (string.IsNullOrEmpty(repo.Host) || string.IsNullOrEmpty(repo.Owner) || string.IsNullOrEmpty(repo.Name)))) {
			throw Invalid();
		}

		var originPrompts = new Dictionary<long, string?>();
		foreach (var file in checkpoint.Files) {
			if (file is null
				|| string.IsNullOrEmpty(file.Path)
				|| string.IsNullOrEmpty(file.DiskHash)
				|| file.Current is null
				|| file.ReviewBaseline is null
				|| file.AcceptedAnchor is null
				|| file.SessionBaseline is null
				|| file.Provenance is null) {
				throw Invalid();
			}

			Validate(file.Current);
			Validate(file.ReviewBaseline);
			Validate(file.AcceptedAnchor);
			Validate(file.SessionBaseline);
			Validate(file.Provenance, originPrompts);
		}
		foreach (var guard in checkpoint.Guards) {
			if (guard is null || string.IsNullOrEmpty(guard.Path) || string.IsNullOrEmpty(guard.DiskHash)) {
				throw Invalid();
			}
		}
	}

	private static void Validate(ReviewTextCheckpoint text) {
		if (string.IsNullOrEmpty(text.Hash) || text.Splices is null) {
			throw Invalid();
		}
		foreach (var splice in text.Splices) {
			if (splice is null || splice.Offset < 0 || splice.DeleteLength < 0 || splice.InsertText is null) {
				throw Invalid();
			}
		}
	}

	private static void Validate(
		ReviewProvenanceCheckpoint provenance,
		Dictionary<long, string?> originPrompts) {
		if (provenance.Origins is null || provenance.Runs is null || provenance.DeletedGaps is null) {
			throw Invalid();
		}
		foreach (var origin in provenance.Origins) {
			if (origin is null || origin.Id <= 0) {
				throw Invalid();
			}
			if (originPrompts.TryGetValue(origin.Id, out string? prompt)
				&& !string.Equals(prompt, origin.Prompt, StringComparison.Ordinal)) {
				throw new InvalidDataException("Review checkpoint assigns inconsistent prompts to one provenance origin.");
			}
			originPrompts[origin.Id] = origin.Prompt;
		}
		foreach (var run in provenance.Runs) {
			if (run is null) {
				throw Invalid();
			}
		}
		foreach (var gap in provenance.DeletedGaps) {
			if (gap is null || gap.Segments is null) {
				throw Invalid();
			}
			foreach (var segment in gap.Segments) {
				if (segment is null || segment.Lines is null || segment.Lines.Any(line => line is null)) {
					throw Invalid();
				}
			}
		}
	}

	private static InvalidDataException Invalid() => new("Review checkpoint has an invalid shape.");
}
