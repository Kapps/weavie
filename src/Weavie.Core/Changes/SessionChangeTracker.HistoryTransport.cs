using System.Collections.Immutable;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private List<BoundaryRestore> CaptureBoundaries(string path, ReviewPart part, LineRange replaced) {
		var restores = new List<BoundaryRestore>();
		foreach (var patch in HistoryFor(path).SelectMany(action => action.PatchesFor(path))
			.Where(patch => patch.Part == part && PathIdentity.Equals(patch.Path, path))) {
			if (ContainsBoundary(replaced, patch.Range.Start))
				restores.Add(new(patch.Id, false, patch.Range.Start - replaced.Start, Touches(patch.Range, replaced)));
			if (ContainsBoundary(replaced, patch.Range.EndExclusive))
				restores.Add(new(patch.Id, true, patch.Range.EndExclusive - replaced.Start, Touches(patch.Range, replaced)));
		}
		return restores;
	}

	private void RestoreBoundaries(List<ReviewPatch> patches) {
		var boundaries = patches.SelectMany(patch => patch.Boundaries.Select(boundary =>
			(boundary.PatchId, boundary.End, Value: patch.Range.Start + boundary.Offset))).ToArray();
		foreach (string path in patches.Select(patch => patch.Path).Distinct(PathIdentity.Comparer)) {
			foreach (var action in HistoryFor(path)) {
				action.ReplacePatches(path, [.. action.PatchesFor(path).Select(target => {
					var range = target.Range;
					foreach (var boundary in boundaries.Where(boundary => boundary.PatchId == target.Id))
						range = boundary.End ? new(range.Start, boundary.Value) : new(boundary.Value, range.EndExclusive);
					return target with { Range = range };
				})]);
			}
		}
	}

	private sealed record BoundaryRestore(long PatchId, bool End, int Offset, bool Covered);

	private void SynchronizeHistory(bool external, ReviewAction? except) {
		foreach (string path in _historyDirty.ToArray()) {
			var current = CaptureHistoryHead(path);
			if (_historyHeads.TryGetValue(path, out var previous)) {
				foreach (var part in new[] { ReviewPart.Review, ReviewPart.Current, ReviewPart.Disk }) {
					var before = previous.Value(part);
					var after = current.Value(part);
					if (before == after) continue;
					var actions = HistoryFor(path).Where(action => !ReferenceEquals(action, except)).ToArray();
					if (actions.Length == 0) continue;
					var lines = TextLines(before);
					var hunks = LineHunker.Hunks(lines, TextLines(after));
					foreach (var action in actions) {
						action.ReplacePatches(path, [.. action.PatchesFor(path).Select(patch => {
							if (!PathIdentity.Equals(patch.Path, path) || patch.Part != part) return patch;
							return patch with {
								Stale = patch.Stale || external && (before.Exists != after.Exists
									|| hunks.Any(hunk => Touches(patch.Range, hunk.BeforeRange))
									|| AmbiguousTransport(lines, patch.Range, hunks)),
								Range = MapRange(patch.Range, hunks),
							};
						})]);
					}
				}
			}
			if (_baseline.ContainsKey(path)) _historyHeads[path] = current;
			else _historyHeads.Remove(path);
			_historyDirty.Remove(path);
		}
	}

	private HistoryHead CaptureHistoryHead(string path) => new(
		new(_reviewBaseline.GetValueOrDefault(path, string.Empty), !_missingReviewBaseline.Contains(path)),
		new(_current.GetValueOrDefault(path, string.Empty), !_missingCurrent.Contains(path)),
		new(_provenance.GetValueOrDefault(path)?.Text ?? _current.GetValueOrDefault(path, string.Empty),
			_baseline.ContainsKey(path) && !_missingCurrent.Contains(path)));

	private sealed record HistoryHead(TextValue Review, TextValue Current, TextValue Disk) {
		public TextValue Value(ReviewPart part) => part switch {
			ReviewPart.Review => Review,
			ReviewPart.Current => Current,
			_ => Disk,
		};
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
			.ToDictionary(pair => pair.Key - range.Start + 1, pair => pair.Value.ToList()));

	private void RestorePatchOrigins(string path, List<ReviewPatch> patches, bool undo) {
		string actual = ReadOrEmpty(path);
		if (!_provenance.TryGetValue(path, out var provenance)) {
			provenance = ProvenanceFile.Empty(actual);
			_provenance[path] = provenance;
		} else provenance = RebaseProvenance(provenance, actual, []);
		var lines = provenance.Lines.ToBuilder();
		var gaps = provenance.DeletedAtGap.ToBuilder();
		int shift = 0;
		foreach (var patch in patches.Where(patch => patch.Part == ReviewPart.Disk).OrderBy(patch => patch.Range.Start)) {
			int length = (undo ? patch.Before : patch.After).Length;
			int start = patch.Range.Start + shift - 1;
			if ((undo ? patch.BeforeOrigins : patch.AfterOrigins) is { } origins) {
				for (int i = 0; i < origins.Lines.Length && start + i < provenance.Lines.Length; i++)
					lines[start + i] = origins.Lines[i];
				foreach (int gap in provenance.DeletedAtGap.Keys.Where(gap => gap >= start && gap <= start + length).ToArray())
					gaps.Remove(gap);
				foreach (var (gap, segments) in origins.Gaps) gaps[start + gap] = [.. segments];
			}
			shift += length - Length(patch.Range);
		}
		_provenance[path] = provenance with { Lines = lines.ToImmutable(), DeletedAtGap = gaps.ToImmutable() };
		SetAllPending(path, false);
		foreach (var hunk in LineHunker.Hunks(SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty)),
			SplitLines(_current.GetValueOrDefault(path, string.Empty))))
			SetPending(path, MapCurrentRangeToActual(path, hunk.AfterRange), true);
	}

	private sealed record OriginSlice(AgentOrigin?[] Lines, Dictionary<int, List<DeletedSegment>> Gaps);
}
