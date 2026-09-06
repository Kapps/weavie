using System.Collections.Immutable;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private IReadOnlyList<RejectedChange> RejectedFor(string path) => _undoStack
		.Where(action => action.Kind == ReviewActionKind.Revert)
		.SelectMany(action => action.Patches.Where(patch => patch.Part == ReviewPart.Current && PathIdentity.Equals(patch.Path, path))
			.Select(patch => new RejectedChange(string.Join(patch.BeforeEol, patch.Before), action.Patches.Any(candidate => candidate.Stale))))
		.ToArray();
	private readonly HistoryStack _undoStack;
	private readonly HistoryStack _redoStack;
	private readonly Dictionary<string, HistoryHead> _historyHeads = new(PathIdentity.Comparer);
	private long _nextActionId;

	/// <summary>Whether a kept decision is available to undo.</summary>
	public bool CanUndoKeep { get { lock (_gate) return _undoStack.Exists(a => a.Kind == ReviewActionKind.Keep); } }
	/// <summary>Whether a rejected decision is available to undo.</summary>
	public bool CanUndoRevert { get { lock (_gate) return _undoStack.Exists(a => a.Kind == ReviewActionKind.Revert); } }
	/// <summary>Whether the review has applied decisions.</summary>
	public bool CanUndo { get { lock (_gate) return _undoStack.Count > 0; } }
	/// <summary>Whether the review has undone decisions.</summary>
	public bool CanRedo { get { lock (_gate) return _redoStack.Count > 0; } }
	/// <summary>Undoes the newest applicable review decision.</summary>
	public ReviewHistoryResult UndoLast() => Reverse(null);
	/// <summary>Undoes the newest applicable keep.</summary>
	public ReviewHistoryResult UndoLastKeep() => Reverse(ReviewActionKind.Keep);
	/// <summary>Restores the newest applicable rejection without overwriting later edits.</summary>
	public ReviewHistoryResult UndoLastRevert() => Reverse(ReviewActionKind.Revert);
	/// <summary>Reapplies the newest applicable undone decision.</summary>
	public ReviewHistoryResult Redo() { lock (_gate) return ApplyHistory(_redoStack, _undoStack, null, undo: false); }
	private ReviewHistoryResult Reverse(ReviewActionKind? kind) {
		lock (_gate) return ApplyHistory(_undoStack, _redoStack, kind, undo: true);
	}

	private ReviewHistoryResult ApplyHistory(HistoryStack source, HistoryStack destination, ReviewActionKind? kind, bool undo) {
		ReconcileReviewDisk();
		bool found = false;
		foreach (var action in source.AsEnumerable().Reverse().ToArray()) {
			if (kind is { } wanted && action.Kind != wanted) continue;
			found = true;
			if (action.Patches.Any(patch => patch.Stale || !PatchHolds(patch, undo)
				|| _undoStack.Where(other => !ReferenceEquals(other, action)).SelectMany(other => other.Patches)
					.Any(cover => cover.Boundaries.Any(boundary => boundary.PatchId == patch.Id && boundary.Covered)))) continue;
			string[] paths = [.. action.Patches.Select(patch => patch.Path).Distinct(PathIdentity.Comparer)];
			var reversed = new ReviewAction(action.Id, action.Kind, action.TouchesDisk, action.Line, []);
			try {
				foreach (string path in paths) {
					var patches = action.Patches.Where(patch => PathIdentity.Equals(patch.Path, path)).ToList();
					var state = Capture(path, withDisk: true);
					var values = patches.GroupBy(patch => patch.Part).ToDictionary(group => group.Key,
						group => ApplyPatches(Value(state, group.Key), [.. group], undo));
					if (action.TouchesDisk && values.TryGetValue(ReviewPart.Disk, out var disk)) {
						if (disk.Exists) _fileSystem.WriteAllText(path, disk.Text);
						else if (_fileSystem.FileExists(path)) _fileSystem.DeleteFile(path);
					}
					if (values.TryGetValue(ReviewPart.Current, out var current)) {
						_current[path] = current.Text;
						SetMissing(_missingCurrent, path, !current.Exists);
					}
					if (values.TryGetValue(ReviewPart.Review, out var review)) {
						_reviewBaseline[path] = review.Text;
						SetMissing(_missingReviewBaseline, path, !review.Exists);
					}
					RestorePatchOrigins(path, patches, undo);
					foreach (var group in patches.GroupBy(patch => patch.Part)) {
						int shift = 0;
						foreach (var patch in group.OrderBy(patch => patch.Range.Start)) {
							int length = (undo ? patch.Before : patch.After).Length;
							int oldLength = Length(patch.Range);
							var range = new LineRange(patch.Range.Start + shift, patch.Range.Start + shift + length);
							int index = patches.IndexOf(patch);
							patches[index] = patch with { Range = range };
							shift += length - oldLength;
						}
					}
					SynchronizeHistory(external: false, except: action);
					if (undo) RestoreBoundaries(patches);
					action.RemovePatches(path);
					reversed.ReplacePatches(path, [.. patches]);
					if (!destination.Contains(reversed)) destination.Add(reversed);
					if (action.PatchCount == 0) source.Remove(action);
					Checkpoint();
					if (action.TouchesDisk) ReportCurrentState(path);
				}
			} catch {
				// A multi-file action retains its successfully written prefix even when a later path fails.
				if (action.PatchCount > 0 && reversed.PatchCount > 0) action.Id = ++_nextActionId;
				Checkpoint();
				throw;
			}
			int? line = action.Line is null ? null : reversed.Patches
				.Where(patch => patch.Part == ReviewPart.Disk)
				.Select(patch => (int?)patch.Range.Start).FirstOrDefault();
			return ReviewHistoryResult.Done(action.TouchesDisk, paths, line);
		}
		Checkpoint();
		return ReviewHistoryResult.Blocked(found);
	}

	private bool PatchHolds(ReviewPatch patch, bool undo) {
		var live = Value(Capture(patch.Path, withDisk: true), patch.Part);
		return live.Exists == (undo ? patch.AfterExists : patch.BeforeExists)
			&& TryGetSlice(TextLines(live), patch.Range, out var slice)
			&& slice.SequenceEqual(undo ? patch.After : patch.Before);
	}

	private static TextValue ApplyPatches(TextValue live, List<ReviewPatch> patches, bool undo) {
		var lines = TextLines(live);
		foreach (var patch in patches.OrderByDescending(patch => patch.Range.Start)) {
			lines.RemoveRange(patch.Range.Start - 1, Length(patch.Range));
			lines.InsertRange(patch.Range.Start - 1, undo ? patch.Before : patch.After);
		}
		string eol = live.Text.Length > 0 ? live.Text : undo ? patches[0].BeforeEol : patches[0].AfterEol;
		return new(JoinLines(lines, eol), undo ? patches[0].BeforeExists : patches[0].AfterExists);
	}

	private void Record(ReviewActionKind kind, bool touchesDisk, int? line, IReadOnlyList<PathState> before) {
		var patches = new List<ReviewPatch>();
		foreach (var previous in before) {
			var current = Capture(previous.Path, withDisk: true);
			foreach (var part in new[] { ReviewPart.Review, ReviewPart.Current, ReviewPart.Disk }) {
				if (part == ReviewPart.Disk && !touchesDisk) continue;
				var from = Value(previous, part);
				var to = Value(current, part);
				var hunks = from.Exists == to.Exists ? LineHunker.Hunks(TextLines(from), TextLines(to))
					: [new LineHunk(new(1, TextLines(from).Count + 1), new(1, TextLines(to).Count + 1))];
				foreach (var hunk in hunks) {
					patches.Add(new(previous.Path, part, hunk.AfterRange,
						[.. Lines(TextLines(from), hunk.BeforeRange)], [.. Lines(TextLines(to), hunk.AfterRange)],
						from.Exists, to.Exists,
						part == ReviewPart.Disk ? CaptureOrigins(previous.Provenance, hunk.BeforeRange) : null,
						part == ReviewPart.Disk ? CaptureOrigins(current.Provenance, hunk.AfterRange) : null) {
						Id = ++_nextActionId,
						Boundaries = CaptureBoundaries(previous.Path, part, hunk.BeforeRange),
						BeforeEol = from.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
						AfterEol = to.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
					});
				}
			}
			// Keeping touches no disk, but its decision still belongs to the exact proposed region.
			if (!touchesDisk) {
				foreach (var patch in patches.Where(patch => patch.Path == previous.Path && patch.Part == ReviewPart.Review).ToArray()) {
					var range = MapRange(patch.Range, LineHunker.Hunks(SplitLines(current.ReviewBaseline), SplitLines(current.Disk)));
					string[] text = [.. Lines(SplitLines(current.Disk), range)];
					patches.Add(new(previous.Path, ReviewPart.Disk, range, text, text, current.OnDisk, current.OnDisk, null, null) { Id = ++_nextActionId });
				}
			}
		}
		SynchronizeHistory(external: false, except: null);
		if (patches.Count > 0) _undoStack.Add(new(++_nextActionId, kind, touchesDisk, line, [.. patches]));
		_redoStack.Clear();
		Checkpoint();
	}

	private static TextValue Value(PathState state, ReviewPart part) => part switch {
		ReviewPart.Review => new(state.ReviewBaseline, state.ReviewBaselineExists),
		ReviewPart.Current => new(state.Current, state.CurrentExists),
		_ => new(state.Disk, state.OnDisk),
	};
	private static List<string> TextLines(TextValue value) => value.Exists ? SplitLines(value.Text) : [];
	private readonly record struct TextValue(string Text, bool Exists);
	private enum ReviewPart { Review, Current, Disk }
	private enum ReviewActionKind { Keep, Revert, Revise }
	private sealed class ReviewAction(long actionId, ReviewActionKind kind, bool touchesDisk, int? line, ImmutableList<ReviewPatch> patches) {
		private readonly Dictionary<string, ImmutableList<ReviewPatch>> _patches = patches
			.GroupBy(patch => patch.Path, PathIdentity.Comparer).ToDictionary(group => group.Key, group => group.ToImmutableList(), PathIdentity.Comparer);
		public event Action<ReviewAction, string?>? Changed;
		public ReviewActionKind Kind { get; } = kind;
		public bool TouchesDisk { get; } = touchesDisk;
		public int? Line { get; } = line;
		public long Id { get; set { field = value; Changed?.Invoke(this, null); } } = actionId;
		public int PatchCount => _patches.Values.Sum(group => group.Count);
		public IEnumerable<string> Paths => _patches.Keys;
		public IEnumerable<ReviewPatch> Patches => _patches.Values.SelectMany(group => group);
		public ImmutableList<ReviewPatch> PatchesFor(string path) => _patches.GetValueOrDefault(path, []);
		public void ReplacePatches(string path, ImmutableList<ReviewPatch> patches) {
			if (PatchesFor(path).SequenceEqual(patches)) return;
			if (patches.Count == 0) _patches.Remove(path);
			else _patches[path] = patches;
			Changed?.Invoke(this, path);
		}
		public void RemovePatches(string path) => ReplacePatches(path, []);
	}

	private sealed record ReviewPatch(string Path, ReviewPart Part, LineRange InitialRange, string[] Before, string[] After,
		bool BeforeExists, bool AfterExists, OriginSlice? BeforeOrigins, OriginSlice? AfterOrigins) {
		public LineRange Range { get; init; } = InitialRange;
		public long Id { get; init; }
		public List<BoundaryRestore> Boundaries { get; init; } = [];
		public bool Stale { get; init; }
		public string BeforeEol { get; init; } = "\n";
		public string AfterEol { get; init; } = "\n";
	}
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
