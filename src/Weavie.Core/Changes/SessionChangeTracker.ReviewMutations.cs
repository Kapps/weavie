using System.Runtime.ExceptionServices;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private void MutateReview(bool rollbackOnFailure, Func<ReviewMutation> mutation) {
		_ = MutateReview(rollbackOnFailure, () => {
			var result = mutation();
			return new ReviewMutation<bool>(true, result.DurableChanged, result.DirtyPaths);
		});
	}

	private T MutateReview<T>(bool rollbackOnFailure, Func<ReviewMutation<T>> mutation) {
		ArgumentNullException.ThrowIfNull(mutation);
		return MutateReviewWithProgress(rollbackOnFailure, _ => mutation());
	}

	private T MutateReviewWithProgress<T>(
		bool rollbackOnFailure,
		Func<ReviewMutationProgress, ReviewMutation<T>> mutation) {
		ArgumentNullException.ThrowIfNull(mutation);
		bool problemsChanged = false;
		ReviewPersistenceException? failure = null;
		ExceptionDispatchInfo? operationFailure = null;
		T result = default!;
		lock (_gate) {
			var before = rollbackOnFailure ? CaptureReviewStateLocked() : null;
			bool pendingBefore = _checkpointPending;
			var pendingPathsBefore = rollbackOnFailure
				? new HashSet<string>(_pendingCheckpointPaths, StringComparer.Ordinal)
				: null;
			var progress = new ReviewMutationProgress();
			ReviewMutation<T> change = default;
			try {
				change = mutation(progress);
				result = change.Result;
			} catch (Exception ex) {
				operationFailure = ExceptionDispatchInfo.Capture(ex);
			}

			if (operationFailure is not null) {
				if (before is not null && progress.Paths.Count == 0) {
					RestoreReviewStateLocked(before);
					_checkpointPending = pendingBefore;
					_pendingCheckpointPaths.Clear();
					_pendingCheckpointPaths.UnionWith(pendingPathsBefore!);
				} else if (progress.Paths.Count > 0) {
					_checkpointPending = true;
					_pendingCheckpointPaths.UnionWith(progress.Paths);
				}

				if (_checkpointPending) {
					try {
						problemsChanged = CommitReviewLocked();
					} catch (Exception ex) when (IsPersistenceFailure(ex)) {
						problemsChanged |= SetProblemLocked(string.Empty, $"Review state could not be saved: {ex.Message}");
					}
				}
			} else {
				bool durableChanged = change.DurableChanged || progress.Paths.Count > 0;
				if (durableChanged) {
					_checkpointPending = true;
					_pendingCheckpointPaths.UnionWith(change.DirtyPaths);
					_pendingCheckpointPaths.UnionWith(progress.Paths);
				}
				if (_checkpointPending) {
					try {
						problemsChanged = CommitReviewLocked();
					} catch (Exception ex) when (IsPersistenceFailure(ex)) {
						if (before is not null && durableChanged && progress.Paths.Count == 0) {
							RestoreReviewStateLocked(before);
							_checkpointPending = pendingBefore;
							_pendingCheckpointPaths.Clear();
							_pendingCheckpointPaths.UnionWith(pendingPathsBefore!);
							failure = new ReviewPersistenceException(
								"Review state could not be saved; the review action was not applied.",
								ex);
						}
						problemsChanged |= SetProblemLocked(string.Empty, $"Review state could not be saved: {ex.Message}");
					}
				}
			}
		}

		if (problemsChanged) {
			ReviewProblemsChanged?.Invoke();
		}
		operationFailure?.Throw();
		if (failure is not null) {
			throw failure;
		}

		return result;
	}
}
