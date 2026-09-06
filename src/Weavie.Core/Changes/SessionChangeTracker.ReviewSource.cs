using Weavie.Core.Review;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	/// <summary>Attaches a PR/ref and extends its baseline without discarding existing review decisions.</summary>
	public void ArmReview(ReviewContext review, IReadOnlyList<ReviewSeed> seeds) {
		lock (_gate) {
			if (_review is { } existing && !existing.SameSource(review))
				throw new InvalidOperationException("This worktree already has a review against a different source.");
			var prepared = seeds.Where(seed => _review is null || !_baseline.ContainsKey(seed.Path)).Select(PrepareSeed).ToArray();
			foreach (var state in prepared) RestoreState(state);
			SynchronizeHistory(external: false, except: null);
			_review = review;
			ReconcileReviewDisk();
			Checkpoint();
		}
	}

	private PathState PrepareSeed(ReviewSeed seed) {
		string path = NormalizePath(seed.Path);
		if (!_isInScope(path)) throw new ArgumentException("The review file is outside this session.");
		var state = Capture(path, withDisk: false);
		if (!state.Tracked) return new(path, true, seed.Baseline, seed.BaselineExists, seed.Current, seed.CurrentExists,
			seed.Baseline, seed.BaselineExists, seed.Baseline, seed.BaselineExists, seed.Current,
			ProvenanceFile.Empty(seed.Current), seed.CurrentExists, seed.Current);
		if (state.Baseline == seed.Baseline && state.BaselineExists == seed.BaselineExists) return state;

		var from = new TextValue(state.Baseline, state.BaselineExists);
		var to = new TextValue(seed.Baseline, seed.BaselineExists);
		var hunks = LineHunker.Hunks(TextLines(from), TextLines(to));
		TextValue Extend(TextValue target) {
			if (target == from) return to;
			var mapping = LineHunker.Hunks(TextLines(from), TextLines(target));
			var patches = new List<ReviewPatch>();
			foreach (var hunk in hunks) {
				var range = MapRange(hunk.BeforeRange, mapping);
				if (mapping.Any(edit => Touches(hunk.BeforeRange, edit.BeforeRange))
					|| AmbiguousTransport(TextLines(from), hunk.BeforeRange, mapping)
					|| !TryGetSlice(TextLines(target), range, out var actual)
					|| !actual.SequenceEqual(Lines(TextLines(from), hunk.BeforeRange)))
					throw new InvalidOperationException("The requested base overlaps kept changes. Your existing review was preserved.");
				patches.Add(new(path, ReviewPart.Review, range, [.. actual], [.. Lines(TextLines(to), hunk.AfterRange)],
					target.Exists, seed.BaselineExists, null, null));
			}
			return patches.Count == 0 ? target : ApplyPatches(target, patches, undo: false);
		}
		var boundary = Extend(new(state.ReviewBaseline, state.ReviewBaselineExists));
		var anchor = Extend(new(state.AcceptedAnchor, state.AcceptedAnchorExists));
		return state with {
			Baseline = seed.Baseline, BaselineExists = seed.BaselineExists,
			ReviewBaseline = boundary.Text, ReviewBaselineExists = boundary.Exists,
			AcceptedAnchor = anchor.Text, AcceptedAnchorExists = anchor.Exists,
		};
	}
}
