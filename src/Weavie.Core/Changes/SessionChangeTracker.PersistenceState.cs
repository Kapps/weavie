using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private ReviewStateSnapshot CaptureReviewStateLocked() => new(
		new Dictionary<string, string>(_baseline, StringComparer.Ordinal),
		new Dictionary<string, string>(_current, StringComparer.Ordinal),
		new Dictionary<string, string>(_reviewBaseline, StringComparer.Ordinal),
		new Dictionary<string, string>(_acceptedAnchor, StringComparer.Ordinal),
		new Dictionary<string, string>(_preEdit, StringComparer.Ordinal),
		new Dictionary<string, FileStat>(_nonText, StringComparer.Ordinal),
		new HashSet<string>(_createdSinceBaseline, StringComparer.Ordinal),
		_provenance.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
		[.. _undoStack],
		[.. _redoStack],
		_currentPrompt,
		_nextOriginId,
		_reviewIdentity,
		_armToken,
		_activeReviewToken,
		[.. _reviewProblems]);

	private void RestoreReviewStateLocked(ReviewStateSnapshot state) {
		Replace(_baseline, state.Baseline);
		Replace(_current, state.Current);
		Replace(_reviewBaseline, state.ReviewBaseline);
		Replace(_acceptedAnchor, state.AcceptedAnchor);
		Replace(_preEdit, state.PreEdit);
		Replace(_nonText, state.NonText);
		_createdSinceBaseline.Clear();
		_createdSinceBaseline.UnionWith(state.CreatedSinceBaseline);
		_provenance.Clear();
		foreach (var (path, provenance) in state.Provenance) {
			_provenance[path] = provenance.Clone();
		}
		_undoStack.Clear();
		_undoStack.AddRange(state.Undo);
		_redoStack.Clear();
		_redoStack.AddRange(state.Redo);
		_currentPrompt = state.CurrentPrompt;
		_nextOriginId = state.NextOriginId;
		_reviewIdentity = state.Identity;
		_armToken = state.ArmToken;
		_activeReviewToken = state.ActiveReviewToken;
		_reviewProblems.Clear();
		_reviewProblems.AddRange(state.Problems);
	}

	private ReviewPathState CaptureReviewPathStateLocked(string path) => new(
		_baseline.TryGetValue(path, out string? baseline),
		baseline ?? string.Empty,
		_current.TryGetValue(path, out string? current),
		current ?? string.Empty,
		_reviewBaseline.TryGetValue(path, out string? reviewBaseline),
		reviewBaseline ?? string.Empty,
		_acceptedAnchor.TryGetValue(path, out string? acceptedAnchor),
		acceptedAnchor ?? string.Empty,
		_createdSinceBaseline.Contains(path),
		CloneProvenance(path));

	private bool ReviewPathChangedLocked(string path, ReviewPathState before) {
		var after = CaptureReviewPathStateLocked(path);
		return before.BaselineTracked != after.BaselineTracked
			|| !string.Equals(before.Baseline, after.Baseline, StringComparison.Ordinal)
			|| before.CurrentTracked != after.CurrentTracked
			|| !string.Equals(before.Current, after.Current, StringComparison.Ordinal)
			|| before.ReviewBaselineTracked != after.ReviewBaselineTracked
			|| !string.Equals(before.ReviewBaseline, after.ReviewBaseline, StringComparison.Ordinal)
			|| before.AcceptedAnchorTracked != after.AcceptedAnchorTracked
			|| !string.Equals(before.AcceptedAnchor, after.AcceptedAnchor, StringComparison.Ordinal)
			|| before.CreatedSinceBaseline != after.CreatedSinceBaseline
			|| !ProvenanceEquals(before.Provenance, after.Provenance);
	}

	private HashSet<string> ReviewStatePathsLocked() {
		var paths = new HashSet<string>(_baseline.Keys, StringComparer.Ordinal);
		paths.UnionWith(_current.Keys);
		paths.UnionWith(_reviewBaseline.Keys);
		paths.UnionWith(_acceptedAnchor.Keys);
		paths.UnionWith(_createdSinceBaseline);
		paths.UnionWith(_provenance.Keys);
		paths.UnionWith(_undoStack.Concat(_redoStack)
			.SelectMany(action => action.Before.Concat(action.After))
			.Select(state => state.Path));
		return paths;
	}

	private static ReviewMutation ReviewChanged(IReadOnlyCollection<string> dirtyPaths) => new(true, dirtyPaths);

	private static ReviewMutation ReviewChanged(string dirtyPath) => new(true, [dirtyPath]);

	private static ReviewMutation ReviewUnchanged() => new(false, []);

	private static ReviewMutation<T> ReviewChanged<T>(T result, IReadOnlyCollection<string> dirtyPaths) =>
		new(result, true, dirtyPaths);

	private static ReviewMutation<T> ReviewChanged<T>(T result, string dirtyPath) =>
		new(result, true, [dirtyPath]);

	private static ReviewMutation<T> ReviewUnchanged<T>(T result) => new(result, false, []);

	private static void Replace<T>(Dictionary<string, T> target, IReadOnlyDictionary<string, T> source) {
		target.Clear();
		foreach (var pair in source) {
			target[pair.Key] = pair.Value;
		}
	}

	private sealed record RestoredReviewFile(
		string Path,
		string Baseline,
		string Current,
		string ReviewBaseline,
		string AcceptedAnchor,
		string Disk,
		ProvenanceFile Provenance,
		bool CreatedSinceBaseline);

	private sealed record ReviewPathState(
		bool BaselineTracked,
		string Baseline,
		bool CurrentTracked,
		string Current,
		bool ReviewBaselineTracked,
		string ReviewBaseline,
		bool AcceptedAnchorTracked,
		string AcceptedAnchor,
		bool CreatedSinceBaseline,
		ProvenanceFile? Provenance);

	private readonly record struct ReviewMutation(bool DurableChanged, IReadOnlyCollection<string> DirtyPaths);

	private readonly record struct ReviewMutation<T>(T Result, bool DurableChanged, IReadOnlyCollection<string> DirtyPaths);

	private sealed class ReviewMutationProgress {
		private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

		public IReadOnlyCollection<string> Paths => _paths;

		public void Applied(string path) => _paths.Add(path);
	}

	private sealed record ReviewStateSnapshot(
		Dictionary<string, string> Baseline,
		Dictionary<string, string> Current,
		Dictionary<string, string> ReviewBaseline,
		Dictionary<string, string> AcceptedAnchor,
		Dictionary<string, string> PreEdit,
		Dictionary<string, FileStat> NonText,
		HashSet<string> CreatedSinceBaseline,
		Dictionary<string, ProvenanceFile> Provenance,
		List<ReviewAction> Undo,
		List<ReviewAction> Redo,
		string? CurrentPrompt,
		long NextOriginId,
		ReviewIdentity? Identity,
		long ArmToken,
		long ActiveReviewToken,
		List<ReviewProblem> Problems);
}
