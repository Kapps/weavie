namespace Weavie.Core.Changes;

/// <summary>
/// The undo/redo history for review actions (keep/revert at hunk/file/all). Each keep or revert pushes a
/// memento of the affected paths' full review state — plus on-disk content for reverts, which mutate the file —
/// so the action can be reversed (undo) or re-applied (redo). Keep-all (<see cref="AcceptTurn"/>) is the commit
/// point: it clears the history. Lives in the same <c>_gate</c> as the rest of the tracker. See
/// <c>docs/specs/turn-review.md</c>.
/// </summary>
public sealed partial class SessionChangeTracker {
	// Applied actions (oldest→newest) and the undone ones available to redo. A new keep/revert clears _redo.
	private readonly List<ReviewAction> _undoStack = [];
	private readonly List<ReviewAction> _redoStack = [];

	/// <summary>Whether there's a keep action to undo (drives Ctrl+Shift+Enter and the toolbar's Undo).</summary>
	public bool CanUndoKeep {
		get { lock (_gate) { return _undoStack.Exists(a => a.Kind == ReviewActionKind.Keep); } }
	}

	/// <summary>Whether there's a revert action to undo (drives Ctrl+Shift+Backspace and the toolbar's Undo).</summary>
	public bool CanUndoRevert {
		get { lock (_gate) { return _undoStack.Exists(a => a.Kind == ReviewActionKind.Revert); } }
	}

	/// <summary>Whether there's any action to undo (drives the toolbar's generic Undo button).</summary>
	public bool CanUndo {
		get { lock (_gate) { return _undoStack.Count > 0; } }
	}

	/// <summary>Whether there's an undone action to redo (drives the toolbar/palette Redo).</summary>
	public bool CanRedo {
		get { lock (_gate) { return _redoStack.Count > 0; } }
	}

	/// <summary>Undoes the most recent still-reversible action of either kind (the toolbar's Undo button).</summary>
	public ReviewHistoryResult UndoLast() => Reverse(null);

	/// <summary>Undoes the most recent still-reversible keep — re-pending its hunk(s). See <see cref="Reverse"/>.</summary>
	public ReviewHistoryResult UndoLastKeep() => Reverse(ReviewActionKind.Keep);

	/// <summary>Undoes the most recent still-reversible revert — restoring its change on disk. See <see cref="Reverse"/>.</summary>
	public ReviewHistoryResult UndoLastRevert() => Reverse(ReviewActionKind.Revert);

	/// <summary>
	/// Re-applies the most recently undone action whose pre-action state still holds (a newer edit to the same
	/// path blocks it). Moves it back onto the undo stack.
	/// </summary>
	public ReviewHistoryResult Redo() {
		lock (_gate) {
			for (int i = _redoStack.Count - 1; i >= 0; i--) {
				var action = _redoStack[i];
				if (!StateHolds(action.Before, action.TouchesDisk)) {
					continue;
				}

				foreach (var state in action.After) {
					RestoreState(state, action.TouchesDisk);
				}

				_redoStack.RemoveAt(i);
				_undoStack.Add(action);
				return ReviewHistoryResult.Done(action.TouchesDisk, Paths(action.After), action.Line);
			}

			return ReviewHistoryResult.Blocked(_redoStack.Count > 0);
		}
	}

	/// <summary>
	/// Undoes the newest action of <paramref name="kind"/> (any kind when null) whose post-action state still
	/// holds (a newer edit to the same path blocks it, so an out-of-order undo can never clobber later work).
	/// Restores its pre-action state — for a revert that means rewriting the file — and moves it onto the redo stack.
	/// </summary>
	private ReviewHistoryResult Reverse(ReviewActionKind? kind) {
		lock (_gate) {
			bool sawKind = false;
			for (int i = _undoStack.Count - 1; i >= 0; i--) {
				var action = _undoStack[i];
				if (kind is { } want && action.Kind != want) {
					continue;
				}

				sawKind = true;
				if (!StateHolds(action.After, action.TouchesDisk)) {
					continue; // a later action moved one of these paths; undoing this one out of order is unsafe
				}

				foreach (var state in action.Before) {
					RestoreState(state, action.TouchesDisk);
				}

				_undoStack.RemoveAt(i);
				_redoStack.Add(action);
				return ReviewHistoryResult.Done(action.TouchesDisk, Paths(action.Before), action.Line);
			}

			return ReviewHistoryResult.Blocked(sawKind);
		}
	}

	// Records an undoable action; the caller (a mutator) supplies the pre-mutation snapshot, the current-side
	// line it acted on (null for file/set scopes), and the paths it touched; this re-snapshots them
	// post-mutation. Pushing a new action invalidates the redo stack. Holds _gate.
	private void Record(ReviewActionKind kind, bool touchesDisk, int? line, IReadOnlyList<PathState> before, IReadOnlyList<string> paths) {
		var after = new List<PathState>(paths.Count);
		foreach (string path in paths) {
			after.Add(Capture(path, touchesDisk));
		}

		_undoStack.Add(new ReviewAction(kind, touchesDisk, line, before, after));
		_redoStack.Clear();
	}

	// Snapshots one path's full review-relevant state (and, for a disk-mutating action, the file's content +
	// existence) so it can be restored verbatim. Holds _gate.
	private PathState Capture(string path, bool withDisk) {
		bool tracked = _current.ContainsKey(path) || _reviewBaseline.ContainsKey(path) || _baseline.ContainsKey(path);
		bool onDisk = withDisk && _fileSystem.FileExists(path);
		return new PathState(
			path,
			tracked,
			_baseline.GetValueOrDefault(path, string.Empty),
			_current.GetValueOrDefault(path, string.Empty),
			_reviewBaseline.GetValueOrDefault(path, string.Empty),
			_acceptedAnchor.GetValueOrDefault(path, string.Empty),
			_preEdit.GetValueOrDefault(path, string.Empty),
			CloneProvenance(path),
			_createdSinceBaseline.Contains(path),
			onDisk,
			onDisk ? _fileSystem.ReadAllText(path) : string.Empty);
	}

	// Restores a captured snapshot: the tracker dictionaries, and — for a disk-mutating action — the file's
	// content (or its absence). An untracked snapshot forgets the path entirely. Holds _gate.
	private void RestoreState(PathState state, bool withDisk) {
		if (state.Tracked) {
			_baseline[state.Path] = state.Baseline;
			_current[state.Path] = state.Current;
			_reviewBaseline[state.Path] = state.ReviewBaseline;
			_acceptedAnchor[state.Path] = state.AcceptedAnchor;
			_preEdit[state.Path] = state.PreEdit;
			RestoreProvenance(state.Path, state.Provenance);
			if (state.Created) {
				_createdSinceBaseline.Add(state.Path);
			} else {
				_createdSinceBaseline.Remove(state.Path);
			}
		} else {
			Forget(state.Path);
		}

		if (!withDisk) {
			return;
		}

		if (state.OnDisk) {
			_fileSystem.WriteAllText(state.Path, state.Disk);
		} else if (_fileSystem.FileExists(state.Path)) {
			_fileSystem.DeleteFile(state.Path);
		}
	}

	// Whether every snapshot still matches the live state on the fields an action mutates (current content, the
	// review baseline, and — for a disk action — the file on disk), so reversing it can't silently clobber a
	// newer edit. Holds _gate.
	private bool StateHolds(IReadOnlyList<PathState> states, bool withDisk) {
		foreach (var state in states) {
			if (state.Tracked) {
				if (!string.Equals(_current.GetValueOrDefault(state.Path, string.Empty), state.Current, StringComparison.Ordinal)
					|| !string.Equals(_reviewBaseline.GetValueOrDefault(state.Path, string.Empty), state.ReviewBaseline, StringComparison.Ordinal)
					|| !ProvenanceEquals(CloneProvenance(state.Path), state.Provenance)) {
					return false;
				}
			} else if (_current.ContainsKey(state.Path)) {
				return false;
			}

			if (withDisk) {
				bool onDisk = _fileSystem.FileExists(state.Path);
				if (onDisk != state.OnDisk
					|| (onDisk && !string.Equals(_fileSystem.ReadAllText(state.Path), state.Disk, StringComparison.Ordinal))) {
					return false;
				}
			}
		}

		return true;
	}

	private static IReadOnlyList<string> Paths(IReadOnlyList<PathState> states) {
		var paths = new List<string>(states.Count);
		foreach (var state in states) {
			paths.Add(state.Path);
		}

		return paths;
	}

	// Whether a keep/revert action mutates disk. Keeps only advance the review baseline; reverts rewrite the file.
	private enum ReviewActionKind {
		Keep,
		Revert,
	}

	// One file's full review state at a point in time. Disk fields are populated only for disk-mutating (revert)
	// actions; Tracked is false for a path absent from the tracker (a created file a revert deleted).
	private sealed record PathState(
		string Path,
		bool Tracked,
		string Baseline,
		string Current,
		string ReviewBaseline,
		string AcceptedAnchor,
		string PreEdit,
		ProvenanceFile? Provenance,
		bool Created,
		bool OnDisk,
		string Disk);

	// One undoable review action: the snapshot of every affected path before and after, so it can be reversed or
	// re-applied uniformly. TouchesDisk decides whether restoring also rewrites the file. Line is the
	// current-side line of the acted hunk (per-hunk actions only) — valid whenever the action is reversible,
	// since StateHolds requires the current content unchanged.
	private sealed record ReviewAction(
		ReviewActionKind Kind,
		bool TouchesDisk,
		int? Line,
		IReadOnlyList<PathState> Before,
		IReadOnlyList<PathState> After);
}

/// <summary>
/// The outcome of an undo/redo: whether it acted, whether the action wrote to disk (so the host knows to reload
/// the file), the affected paths (to re-push their diffs), the current-side line the action acted on (per-hunk
/// actions only — so the host lands the editor on the restored change, not the file's first hunk), and whether
/// it was blocked by a newer edit (vs simply having nothing to do).
/// </summary>
public readonly record struct ReviewHistoryResult(bool Acted, bool TouchedDisk, IReadOnlyList<string> Paths, int? Line, bool WasBlocked) {
	/// <summary>An undo/redo that ran, naming the disk involvement, the paths it changed, and the acted hunk's line (null for file/set scopes).</summary>
	public static ReviewHistoryResult Done(bool touchedDisk, IReadOnlyList<string> paths, int? line) => new(true, touchedDisk, paths, line, false);

	/// <summary>An undo/redo that didn't run — <paramref name="blocked"/> distinguishes "newer edit in the way" from "nothing to do".</summary>
	public static ReviewHistoryResult Blocked(bool blocked) => new(false, false, [], null, blocked);
}
