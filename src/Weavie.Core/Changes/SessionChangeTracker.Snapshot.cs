namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	// One checkpoint per path; actions retain only their changed regions.
	private PathState Capture(string path, bool withDisk) {
		bool tracked = _acceptedAnchor.ContainsKey(path);
		bool onDisk = withDisk ? _fileSystem.FileExists(path) : tracked && !_missingCurrent.Contains(path);
		return new PathState(
			path,
			tracked,
			_current.GetValueOrDefault(path, string.Empty),
			!_missingCurrent.Contains(path),
			_reviewBaseline.GetValueOrDefault(path, string.Empty),
			!_missingReviewBaseline.Contains(path),
			_acceptedAnchor.GetValueOrDefault(path, string.Empty),
			!_missingAcceptedAnchor.Contains(path),
			_preEdit.GetValueOrDefault(path, string.Empty),
			_provenance.GetValueOrDefault(path),
			onDisk,
			onDisk ? withDisk ? _fileSystem.ReadAllText(path) : _provenance.GetValueOrDefault(path)?.Text ?? _current.GetValueOrDefault(path, string.Empty) : string.Empty);
	}

	private void RestoreState(PathState state) {
		if (state.Tracked) {
			_current[state.Path] = state.Current;
			SetMissing(_missingCurrent, state.Path, !state.CurrentExists);
			_reviewBaseline[state.Path] = state.ReviewBaseline;
			SetMissing(_missingReviewBaseline, state.Path, !state.ReviewBaselineExists);
			_acceptedAnchor[state.Path] = state.AcceptedAnchor;
			SetMissing(_missingAcceptedAnchor, state.Path, !state.AcceptedAnchorExists);
		} else {
			ForgetReview(state.Path);
		}
		_preEdit[state.Path] = state.PreEdit;
		RestoreProvenance(state.Path, state.Provenance);
	}

	// One file's full review state at a point in time. Disk fields are populated only for disk-mutating (revert)
	// actions; Tracked is false for a path the review doesn't hold.
	private sealed record PathState(
		string Path,
		bool Tracked,
		string Current,
		bool CurrentExists,
		string ReviewBaseline,
		bool ReviewBaselineExists,
		string AcceptedAnchor,
		bool AcceptedAnchorExists,
		string PreEdit,
		ProvenanceFile? Provenance,
		bool OnDisk,
		string Disk);

}
