using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private List<BoundaryRestore> CaptureBoundaries(string path, ReviewPart part, LineRange replaced) {
		var restores = new List<BoundaryRestore>();
		foreach (var patch in _undoStack.Concat(_redoStack).SelectMany(action => action.Patches)
			.Where(patch => patch.Part == part && PathIdentity.Equals(patch.Path, path))) {
			if (ContainsBoundary(replaced, patch.Range.Start))
				restores.Add(new(patch.Id, false, patch.Range.Start - replaced.Start, Touches(patch.Range, replaced)));
			if (ContainsBoundary(replaced, patch.Range.EndExclusive))
				restores.Add(new(patch.Id, true, patch.Range.EndExclusive - replaced.Start, Touches(patch.Range, replaced)));
		}
		return restores;
	}

	private void RestoreBoundaries(List<ReviewPatch> patches) {
		var targets = _undoStack.Concat(_redoStack).SelectMany(action => action.Patches).ToDictionary(patch => patch.Id);
		foreach (var patch in patches) {
			foreach (var boundary in patch.Boundaries) {
				if (!targets.TryGetValue(boundary.PatchId, out var target)) continue;
				int value = patch.Range.Start + boundary.Offset;
				target.Range = boundary.End ? new(target.Range.Start, value) : new(value, target.Range.EndExclusive);
			}
		}
	}

	private sealed record BoundaryRestore(long PatchId, bool End, int Offset, bool Covered);

	private void SynchronizeHistory(bool external, ReviewAction? except) {
		foreach (string path in _historyHeads.Keys.Union(_baseline.Keys, PathIdentity.Comparer).ToArray()) {
			var current = Capture(path, withDisk: false);
			if (_historyHeads.TryGetValue(path, out var previous)) {
				foreach (var part in new[] { ReviewPart.Review, ReviewPart.Current, ReviewPart.Disk }) {
					var before = Value(previous, part);
					var after = Value(current, part);
					if (before == after) continue;
					var lines = TextLines(before);
					var hunks = LineHunker.Hunks(lines, TextLines(after));
					foreach (var action in _undoStack.Concat(_redoStack)) {
						if (ReferenceEquals(action, except)) continue;
						foreach (var patch in action.Patches.Where(patch => PathIdentity.Equals(patch.Path, path) && patch.Part == part)) {
							if (external && (before.Exists != after.Exists
								|| hunks.Any(hunk => Touches(patch.Range, hunk.BeforeRange))
								|| AmbiguousTransport(lines, patch.Range, hunks))) patch.Stale = true;
							patch.Range = MapRange(patch.Range, hunks);
						}
					}
				}
			}
			_historyHeads[path] = current;
		}
	}

	private static bool Touches(LineRange decision, LineRange edit) => Length(decision) == 0
		? ContainsBoundary(edit, decision.Start)
		: Length(edit) == 0 ? edit.Start > decision.Start && edit.Start < decision.EndExclusive
		: decision.Start < edit.EndExclusive && edit.Start < decision.EndExclusive;

	// Repeated equal text cannot identify which occurrence survived a snapshot-only edit.
	private static bool AmbiguousTransport(IReadOnlyList<string> lines, LineRange range, IReadOnlyList<LineHunk> hunks) {
		var target = Lines(lines, range);
		if (target.Count == 0) return false;
		int matches = 0;
		for (int i = 0; i + target.Count <= lines.Count; i++) {
			if (lines.Skip(i).Take(target.Count).SequenceEqual(target)) matches++;
		}
		return matches > 1 && hunks.Any(hunk => Lines(lines, hunk.BeforeRange).Intersect(target).Any());
	}

	private static OriginSlice? CaptureOrigins(ProvenanceFile? provenance, LineRange range) => provenance is null ? null : new(
		provenance.Lines.Skip(range.Start - 1).Take(Length(range)).ToArray(),
		provenance.DeletedAtGap.Where(pair => ContainsBoundary(range, pair.Key + 1))
			.ToDictionary(pair => pair.Key - range.Start + 1, pair => pair.Value));

	private void RestorePatchOrigins(string path, List<ReviewPatch> patches, bool undo) {
		string actual = ReadOrEmpty(path);
		if (!_provenance.TryGetValue(path, out var provenance)) {
			provenance = ProvenanceFile.Empty(actual);
			_provenance[path] = provenance;
		} else RebaseProvenance(provenance, actual, []);
		int shift = 0;
		foreach (var patch in patches.Where(patch => patch.Part == ReviewPart.Disk).OrderBy(patch => patch.Range.Start)) {
			int length = (undo ? patch.Before : patch.After).Length;
			int start = patch.Range.Start + shift - 1;
			if ((undo ? patch.BeforeOrigins : patch.AfterOrigins) is { } origins) {
				for (int i = 0; i < origins.Lines.Length && start + i < provenance.Lines.Count; i++)
					provenance.Lines[start + i] = origins.Lines[i];
				foreach (int gap in provenance.DeletedAtGap.Keys.Where(gap => gap >= start && gap <= start + length).ToArray())
					provenance.DeletedAtGap.Remove(gap);
				foreach (var (gap, segments) in origins.Gaps) provenance.DeletedAtGap[start + gap] = [.. segments];
			}
			shift += length - Length(patch.Range);
		}
		SetAllPending(path, false);
		foreach (var hunk in LineHunker.Hunks(SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty)),
			SplitLines(_current.GetValueOrDefault(path, string.Empty))))
			SetPending(path, MapCurrentRangeToActual(path, hunk.AfterRange), true);
	}

	private sealed record OriginSlice(AgentOrigin?[] Lines, Dictionary<int, List<DeletedSegment>> Gaps);
}
