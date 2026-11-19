using Weavie.Core.FileSystem;
using Weavie.Core.Review;

namespace Weavie.Core.Changes;

/// <summary>A reserved review listing and the paths joined while it was read (see docs/specs/review-state.md).</summary>
public sealed class ReviewRequest(bool relist) {
	/// <summary>Whether this listing re-lists an open review rather than starting one the user asked for.</summary>
	public bool Relist { get; } = relist;
	internal HashSet<string> Joined { get; } = new(PathIdentity.Comparer);
}

/// <summary>A file joining a git review, with its before read at the review's base.</summary>
public readonly record struct ReviewJoin(string Path, string Before, bool BeforeExists);

public sealed partial class SessionChangeTracker {
	private ReviewRequest? _reviewRequest;
	// Paths the open git review never holds (ignored), so their saves stop asking git for a before.
	private readonly HashSet<string> _unjoinable = new(PathIdentity.Comparer);

	/// <summary>Raised under the tracker's lock when the review's source changes; handlers may only signal.</summary>
	public event Action? ReviewChanged;

	/// <summary>Reserves a review the user started, superseding any listing still reading git.</summary>
	public ReviewRequest BeginReviewRequest() {
		lock (_gate) return _reviewRequest = new ReviewRequest(false);
	}

	/// <summary>
	/// Reserves a re-list of <paramref name="expected"/>, superseding an earlier re-list; null when that review is no
	/// longer open or a review the user started is still being listed.
	/// </summary>
	public ReviewRequest? BeginRelist(ReviewContext expected) {
		lock (_gate) {
			if (_review != expected || _reviewRequest is { Relist: false }) return null;
			return _reviewRequest = new ReviewRequest(true);
		}
	}

	/// <summary>Releases <paramref name="request"/> if it is still pending, so a failed listing blocks no re-list.</summary>
	public void EndReviewRequest(ReviewRequest request) {
		lock (_gate) {
			if (ReferenceEquals(_reviewRequest, request)) _reviewRequest = null;
		}
	}

	/// <summary>Closes <paramref name="expected"/> like <see cref="CloseReview"/>; another open review is left alone.</summary>
	public bool RetireReview(ReviewContext expected) {
		lock (_gate) {
			if (_review != expected) return false;
			CloseReviewLocked(accepted: false);
			Checkpoint();
			return true;
		}
	}

	/// <summary>Scraps the review: its files, decisions, and source go; nothing is approved.</summary>
	public void CloseReview() {
		lock (_gate) {
			CloseReviewLocked(accepted: false);
			Checkpoint();
		}
	}

	// A default review with nothing pending has nothing left to show; a git review stays open with everything kept.
	private bool CompleteAcceptanceLocked() {
		if (_review is not null || _current.Any(file => !_nonText.ContainsKey(file.Key)
			&& ((_reviewBaseline.GetValueOrDefault(file.Key, string.Empty) != file.Value
					&& LineDiff.FirstChangedLine(_reviewBaseline.GetValueOrDefault(file.Key, string.Empty), file.Value) is not null)
				|| _missingReviewBaseline.Contains(file.Key) != _missingCurrent.Contains(file.Key)))) return false;
		CloseReviewLocked(accepted: true);
		return true;
	}

	// Provenance outlives the review only while it has pending agent lines a later edit could correct.
	private void CloseReviewLocked(bool accepted) {
		foreach (string path in _acceptedAnchor.Keys.ToArray()) {
			if (accepted) SetAllPending(path, false);
			ForgetReview(path);
		}
		foreach (var (path, provenance) in _provenance.ToArray()) {
			if (!provenance.HasPending) _provenance.Remove(path);
		}
		_nonText.Clear();
		_unjoinable.Clear();
		_undoStack.Clear();
		_redoStack.Clear();
		SetReview(null);
		// A re-list belongs to the review that just went; a review the user started still opens.
		if (_reviewRequest is { Relist: true }) _reviewRequest = null;
	}

	/// <summary>
	/// Replaces the review with <paramref name="review"/>'s git listing unless a newer request or a close superseded
	/// <paramref name="request"/>. A held path keeps its kept text and history only when its before is unchanged; every
	/// path missing from <paramref name="seeds"/> is forgotten. Answers the paths joined while the listing was read, for
	/// the caller to join again, or null when superseded.
	/// </summary>
	public IReadOnlyCollection<string>? ReplaceReview(ReviewContext review, IReadOnlyList<ReviewSeed> seeds, ReviewRequest request) {
		ArgumentNullException.ThrowIfNull(review);
		ArgumentNullException.ThrowIfNull(seeds);
		lock (_gate) {
			if (!ReferenceEquals(_reviewRequest, request)) return null;
			_reviewRequest = null;
			_unjoinable.Clear();
			var listed = new HashSet<string>(PathIdentity.Comparer);
			foreach (var seed in seeds) {
				string path = NormalizePath(seed.Path);
				if (!_isInScope(path)) throw new ArgumentException("The review file is outside this session.");
				listed.Add(path);
				if (!_acceptedAnchor.TryGetValue(path, out string? before) || before != seed.Baseline
					|| _missingAcceptedAnchor.Contains(path) == seed.BaselineExists) {
					DropHistory(path);
					HoldBefore(path, seed.Baseline, seed.BaselineExists);
				}
				SetText(_current, _missingCurrent, path, seed.Current, seed.CurrentExists);
				_nonText.Remove(path);
				// Only an explicit Keep settles agent lines; listing marks what this review shows as pending.
				MarkReviewPending(path);
			}
			foreach (string path in _acceptedAnchor.Keys.Where(path => !listed.Contains(path)).ToArray()) {
				DropHistory(path);
				ForgetReview(path);
			}
			SynchronizeHistory(external: true, except: null);
			SetReview(review);
			Checkpoint();
			return request.Joined;
		}
	}

	/// <summary>
	/// Refreshes a path <paramref name="expected"/> holds from disk. False when the caller must read the path's before
	/// from git and call <see cref="JoinReview"/>: the review isn't the expected one, or it neither holds the path nor
	/// knows it can't.
	/// </summary>
	public bool RefreshReviewPath(ReviewContext expected, string path) {
		path = NormalizePath(path);
		lock (_gate) {
			if (_review != expected) return false;
			if (_unjoinable.Contains(path)) return true;
			if (!_acceptedAnchor.ContainsKey(path)) return false;
			_reviewRequest?.Joined.Add(path);
			ReconcilePath(path);
			Checkpoint();
			return true;
		}
	}

	/// <summary>
	/// Adds worktree files to <paramref name="expected"/>, each current from disk. Answers every join when the review
	/// changed meanwhile, else none; an ignored or out-of-worktree file is remembered as one the review can't hold.
	/// </summary>
	public IReadOnlyList<ReviewJoin> JoinReview(ReviewContext expected, IReadOnlyList<ReviewJoin> joins) {
		ArgumentNullException.ThrowIfNull(joins);
		ReviewJoin[] normalized = [.. joins.Select(join => join with { Path = NormalizePath(join.Path) })];
		var outside = normalized.Where(join => !_isInScope(join.Path) || !PathBoundary.Contains(_workspaceRoot, join.Path))
			.Select(join => join.Path).ToHashSet(PathIdentity.Comparer);
		// One ignore query for every file new at the base; a file the base has is tracked, so never ignored.
		var ignored = _ignoredAmong([.. normalized.Where(join => !join.BeforeExists && !outside.Contains(join.Path)).Select(join => join.Path)]);
		lock (_gate) {
			if (_review != expected) return normalized;
			foreach (var join in normalized) {
				if (outside.Contains(join.Path) || ignored.Contains(join.Path)) {
					_unjoinable.Add(join.Path);
					continue;
				}
				_reviewRequest?.Joined.Add(join.Path);
				if (!_acceptedAnchor.ContainsKey(join.Path)) HoldBefore(join.Path, join.Before, join.BeforeExists);
				ReconcilePath(join.Path);
				MarkReviewPending(join.Path);
			}
			Checkpoint();
			return [];
		}
	}

	/// <summary>
	/// The paths the review doesn't hold or doesn't show as they are on disk; the rest of a watcher batch is an echo.
	/// Disk is read outside the tracker's lock.
	/// </summary>
	public IReadOnlyList<string> UnaccountedFor(IEnumerable<string> paths) {
		ArgumentNullException.ThrowIfNull(paths);
		string[] candidates;
		HashSet<string> held;
		lock (_gate) {
			candidates = [.. paths.Select(NormalizePath).Where(path => !_unjoinable.Contains(path))];
			held = candidates.Where(_current.ContainsKey).ToHashSet(PathIdentity.Comparer);
		}
		var disk = held.ToDictionary(path => path, path => {
			bool exists = _fileSystem.FileExists(path);
			return (Exists: exists, Text: exists && _fileSystem.TryReadAllText(path, out string text) ? text : null);
		}, PathIdentity.Comparer);
		lock (_gate) {
			return [.. candidates.Where(path => !disk.TryGetValue(path, out var seen) || !_current.TryGetValue(path, out string? current)
				|| seen.Exists == _missingCurrent.Contains(path) || seen.Exists && seen.Text != current)];
		}
	}

	// Whether a git review's held paths still show what disk holds, so a whole-file action acts only on what the user
	// saw; the default review always reconciles first. Caller holds _gate.
	private bool ShownMatchesDisk(IEnumerable<string> paths) =>
		_review is null || paths.All(path => !_current.ContainsKey(path) || ShowsDisk(path));

	private bool ShowsDisk(string path) {
		bool exists = _fileSystem.FileExists(path);
		return exists != _missingCurrent.Contains(path)
			&& (!exists || _fileSystem.TryReadAllText(path, out string actual) && actual == _current[path]);
	}

	private void HoldBefore(string path, string before, bool exists) {
		SetText(_acceptedAnchor, _missingAcceptedAnchor, path, before, exists);
		SetText(_reviewBaseline, _missingReviewBaseline, path, before, exists);
	}

	private static void SetText(TrackedMap<string> texts, TrackedSet missing, string path, string text, bool exists) {
		texts[path] = text;
		SetMissing(missing, path, !exists);
	}

	private void DropHistory(string path) {
		foreach (var action in HistoryFor(path)) {
			_undoStack.Remove(action);
			_redoStack.Remove(action);
		}
	}

	private void SetReview(ReviewContext? review) {
		bool changed = _review != review;
		_review = review;
		if (changed) ReviewChanged?.Invoke();
	}
}
