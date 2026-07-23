namespace Weavie.Core.Changes;

/// <summary>A correction captured at the user's action and attributed to the producing prompt.</summary>
/// <param name="RelativePath">The file's workspace-root-relative path with <c>/</c> separators.</param>
/// <param name="Before">The agent-authored text the user changed.</param>
/// <param name="After">The user's replacement text.</param>
/// <param name="Prompt">The prompt that produced the corrected text; null when the provider reports none.</param>
/// <param name="OriginId">The agent region's stable id, so successive editor saves over one region coalesce into a single correction rather than recording each intermediate keystroke-save.</param>
/// <param name="Continuable">True for an editor save (which autosave repeats as the user types, so it may be superseded by a later save); false for a discrete review-UI revert.</param>
public sealed record CorrectionEdit(string RelativePath, string Before, string After, string? Prompt, long OriginId, bool Continuable);

public sealed partial class SessionChangeTracker {
	private string? _currentPrompt;
	private long _nextOriginId;
	private readonly Dictionary<string, ProvenanceFile> _provenance = new(StringComparer.Ordinal);

	/// <summary>Raised outside the tracker lock with one user's action-time corrections.</summary>
	public event Action<IReadOnlyList<CorrectionEdit>>? Corrected;

	/// <summary>Captures a saved editor change only where it changes pending agent-authored text.</summary>
	/// <param name="path">Absolute path of the saved file.</param>
	/// <param name="content">The file's complete saved content.</param>
	public void RecordHandEdit(string path, string content) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(content);
		List<CorrectionEdit> edits = [];
		MutateReview(rollbackOnFailure: false, () => {
			var before = CaptureReviewPathStateLocked(path);
			if (!_current.TryGetValue(path, out string? reviewCurrent)
				|| !_provenance.TryGetValue(path, out var provenance)) {
				return ReviewUnchanged();
			}

			string[] beforeLines = LineDiff.SplitLines(provenance.Text);
			string[] afterLines = LineDiff.SplitLines(content);
			string previousText = provenance.Text;
			var attributed = new List<AttributedChange>();
			foreach (var hunk in LineHunker.Hunks(beforeLines, afterLines)) {
				foreach (var change in UserChanges(provenance, hunk, afterLines)) {
					attributed.Add(change);
					edits.Add(new CorrectionEdit(
						Relativize(path),
						Slice(beforeLines, change.BeforeRange),
						Slice(afterLines, change.AfterRange),
						change.Origin.Prompt,
						change.Origin.Id,
						Continuable: true));
				}
			}

			RebaseProvenance(provenance, content, attributed);
			if (attributed.Count > 0) {
				_current[path] = ApplyChanges(previousText, reviewCurrent, attributed);
			}

			return ReviewPathChangedLocked(path, before) ? ReviewChanged(path) : ReviewUnchanged();
		});

		RaiseCorrected(edits);
	}

	private void CaptureProvenanceBaseline(string path, string content) {
		if (_provenance.TryGetValue(path, out var provenance)) {
			RebaseProvenance(provenance, content, []);
		} else {
			_provenance[path] = ProvenanceFile.Empty(content);
		}
	}

	private string RecordAgentProvenance(string path, string before, string after, string reviewCurrent) {
		if (!_provenance.TryGetValue(path, out var provenance)) {
			provenance = ProvenanceFile.Empty(before);
			_provenance[path] = provenance;
		} else if (!string.Equals(provenance.Text, before, StringComparison.Ordinal)) {
			RebaseProvenance(provenance, before, []);
		}

		string[] afterLines = LineDiff.SplitLines(after);
		var hunks = LineHunker.Hunks(LineDiff.SplitLines(before), afterLines);
		if (hunks.Count == 0) {
			return reviewCurrent;
		}

		var origin = new AgentOrigin(_currentPrompt, true, ++_nextOriginId);
		var changes = hunks
			.Select(hunk => new AttributedChange(hunk.BeforeRange, hunk.AfterRange, Lines(afterLines, hunk.AfterRange), origin))
			.ToList();
		string updated = ApplyChanges(before, reviewCurrent, changes);
		RebaseProvenance(provenance, after, changes);
		return updated;
	}

	private void SeedProvenance(string path, string content) => _provenance[path] = ProvenanceFile.Empty(content);

	private static void RebaseProvenance(ProvenanceFile provenance, string content, IReadOnlyList<AttributedChange> changes) {
		string previous = provenance.Text;
		string[] beforeLines = LineDiff.SplitLines(previous);
		string[] afterLines = LineDiff.SplitLines(content);
		var hunks = LineHunker.Hunks(beforeLines, afterLines);
		var origins = new List<AgentOrigin?>(Enumerable.Repeat<AgentOrigin?>(null, afterLines.Length));
		int beforeIndex = 0;
		int afterIndex = 0;
		foreach (var op in LineHunker.Align(beforeLines, afterLines)) {
			switch (op.Kind) {
				case LineHunker.LineOpKind.Equal:
					origins[afterIndex] = provenance.Lines[beforeIndex];
					beforeIndex++;
					afterIndex++;
					break;
				case LineHunker.LineOpKind.Delete:
					beforeIndex++;
					break;
				case LineHunker.LineOpKind.Insert:
					afterIndex++;
					break;
			}
		}

		var gaps = new Dictionary<int, List<DeletedSegment>>();
		foreach (var (gap, segments) in provenance.DeletedAtGap) {
			var touching = hunks.FirstOrDefault(hunk => ContainsBoundary(hunk.BeforeRange, gap + 1));
			var remaining = touching.BeforeRange.Start == touching.BeforeRange.EndExclusive
				&& touching.AfterRange.Start != touching.AfterRange.EndExclusive
				? RemainingSegments(segments, Lines(afterLines, touching.AfterRange))
				: segments;
			if (remaining.Count == 0) {
				continue;
			}
			int mapped = MapBoundary(gap + 1, hunks, endBias: false) - 1;
			gaps[mapped] = [.. remaining];
		}

		foreach (var change in changes) {
			for (int i = change.AfterRange.Start - 1; i < change.AfterRange.EndExclusive - 1; i++) {
				origins[i] = change.Origin;
			}
			if (change.AfterRange.Start == change.AfterRange.EndExclusive) {
				AddGap(gaps, change.AfterRange.Start - 1, new DeletedSegment(change.Origin, Lines(beforeLines, change.BeforeRange)));
			}
		}

		provenance.Text = content;
		provenance.Lines = origins;
		provenance.DeletedAtGap = gaps;
	}

	private static AgentOrigin? EligibleOrigin(ProvenanceFile provenance, LineHunk hunk) {
		if (hunk.BeforeRange.Start == hunk.BeforeRange.EndExclusive) {
			int gap = hunk.BeforeRange.Start - 1;
			if (gap > 0 && gap < provenance.Lines.Count
				&& !(gap == provenance.Lines.Count - 1 && LineDiff.SplitLines(provenance.Text)[^1].Length == 0)
				&& provenance.Lines[gap - 1] is { Pending: true } left
				&& provenance.Lines[gap] is { Pending: true } right
				&& left.Id == right.Id) {
				return left;
			}
			return null;
		}

		AgentOrigin? origin = null;
		for (int i = hunk.BeforeRange.Start - 1; i < hunk.BeforeRange.EndExclusive - 1; i++) {
			if (provenance.Lines[i] is not { Pending: true } lineOrigin
				|| (origin is not null && origin.Id != lineOrigin.Id)) {
				return null;
			}
			origin = lineOrigin;
		}
		return origin;
	}

	private static IReadOnlyList<AttributedChange> UserChanges(
		ProvenanceFile provenance,
		LineHunk hunk,
		IReadOnlyList<string> afterLines) {
		if (Length(hunk.BeforeRange) == 0) {
			int gap = hunk.BeforeRange.Start - 1;
			if (provenance.DeletedAtGap.TryGetValue(gap, out var deleted)) {
				var restored = new List<AttributedChange>();
				var insertedLines = Lines(afterLines, hunk.AfterRange);
				foreach (var match in MatchSegments(deleted, insertedLines)) {
					if (match.Segment.Origin.Pending) {
						var after = new LineRange(
							hunk.AfterRange.Start + match.InsertedStart,
							hunk.AfterRange.Start + match.InsertedStart + match.Count);
						restored.Add(new AttributedChange(hunk.BeforeRange, after, Lines(afterLines, after), match.Segment.Origin));
					}
				}
				return restored;
			}
			return EligibleOrigin(provenance, hunk) is { } inserted
				? [new AttributedChange(hunk.BeforeRange, hunk.AfterRange, Lines(afterLines, hunk.AfterRange), inserted)]
				: [];
		}

		var lineOrigins = new List<AgentOrigin?>();
		for (int i = hunk.BeforeRange.Start - 1; i < hunk.BeforeRange.EndExclusive - 1; i++) {
			lineOrigins.Add(provenance.Lines[i] is { Pending: true } origin ? origin : null);
		}
		var common = lineOrigins[0];
		if (common is not null && lineOrigins.All(origin => origin?.Id == common.Id)) {
			return [new AttributedChange(hunk.BeforeRange, hunk.AfterRange, Lines(afterLines, hunk.AfterRange), common)];
		}

		var changes = new List<AttributedChange>();
		int paired = Math.Min(Length(hunk.BeforeRange), Length(hunk.AfterRange));
		for (int i = 0; i < lineOrigins.Count; i++) {
			if (lineOrigins[i] is not { } origin) {
				continue;
			}
			var before = new LineRange(hunk.BeforeRange.Start + i, hunk.BeforeRange.Start + i + 1);
			var after = i < paired
				? new LineRange(hunk.AfterRange.Start + i, hunk.AfterRange.Start + i + 1)
				: new LineRange(hunk.AfterRange.EndExclusive, hunk.AfterRange.EndExclusive);
			changes.Add(new AttributedChange(before, after, Lines(afterLines, after), origin));
		}
		return changes;
	}

	private static IReadOnlyList<SegmentMatch> MatchSegments(
		IReadOnlyList<DeletedSegment> segments,
		IReadOnlyList<string> insertedLines) {
		var matches = new List<SegmentMatch>();
		bool[] used = new bool[insertedLines.Count];
		foreach (var segment in segments) {
			SegmentMatch? best = null;
			for (int deletedStart = 0; deletedStart < segment.Lines.Count; deletedStart++) {
				for (int insertedStart = 0; insertedStart < insertedLines.Count; insertedStart++) {
					int count = 0;
					while (deletedStart + count < segment.Lines.Count
						&& insertedStart + count < insertedLines.Count
						&& !used[insertedStart + count]
						&& string.Equals(segment.Lines[deletedStart + count], insertedLines[insertedStart + count], StringComparison.Ordinal)) {
						count++;
					}
					if (count > (best?.Count ?? 0)) {
						best = new SegmentMatch(segment, deletedStart, insertedStart, count);
					}
				}
			}
			if (best is { Count: > 0 } match) {
				for (int i = 0; i < match.Count; i++) {
					used[match.InsertedStart + i] = true;
				}
				matches.Add(match);
			}
		}
		return matches;
	}

	private static IReadOnlyList<DeletedSegment> RemainingSegments(
		IReadOnlyList<DeletedSegment> segments,
		IReadOnlyList<string> insertedLines) {
		var matches = MatchSegments(segments, insertedLines).ToDictionary(match => match.Segment);
		var remaining = new List<DeletedSegment>();
		foreach (var segment in segments) {
			if (!matches.TryGetValue(segment, out var match)) {
				remaining.Add(segment);
				continue;
			}
			if (match.DeletedStart > 0) {
				remaining.Add(segment with { Lines = segment.Lines.GetRange(0, match.DeletedStart) });
			}
			int after = match.DeletedStart + match.Count;
			if (after < segment.Lines.Count) {
				remaining.Add(segment with { Lines = segment.Lines.GetRange(after, segment.Lines.Count - after) });
			}
		}
		return remaining;
	}

	private static string ApplyChanges(string source, string target, IReadOnlyList<AttributedChange> changes) {
		if (changes.Count == 0) {
			return target;
		}

		string[] sourceLines = LineDiff.SplitLines(source);
		var targetLines = LineDiff.SplitLines(target).ToList();
		var mapping = LineHunker.Hunks(sourceLines, targetLines);
		var patches = changes
			.Select(change => new ProjectedChange(MapRange(change.BeforeRange, mapping), change.AfterLines))
			.OrderByDescending(change => change.Range.Start)
			.ToList();
		foreach (var patch in patches) {
			targetLines.RemoveRange(patch.Range.Start - 1, patch.Range.EndExclusive - patch.Range.Start);
			targetLines.InsertRange(patch.Range.Start - 1, patch.AfterLines);
		}
		return string.Join(target.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", targetLines);
	}

	private PreparedReviewChange PrepareReviewChange(string path, string before, string after) {
		if (!_provenance.TryGetValue(path, out var provenance)) {
			return new PreparedReviewChange(after, null);
		}
		var preparedProvenance = provenance.Clone();
		var marker = new AgentOrigin(null, false, long.MinValue);
		string[] afterLines = LineDiff.SplitLines(after);
		var changes = LineHunker.Hunks(LineDiff.SplitLines(before), afterLines)
			.Select(hunk => new AttributedChange(hunk.BeforeRange, hunk.AfterRange, Lines(afterLines, hunk.AfterRange), marker))
			.ToList();
		string actual = ApplyChanges(before, provenance.Text, changes);
		RebaseProvenance(preparedProvenance, actual, []);
		return new PreparedReviewChange(actual, preparedProvenance);
	}

	private List<CorrectionEdit> CorrectionsForRevert(string path, LineRange currentRange, LineRange baselineRange) {
		var edits = new List<CorrectionEdit>();
		if (!_provenance.TryGetValue(path, out var provenance)
			|| !_current.TryGetValue(path, out string? current)) {
			return edits;
		}

		string[] currentLines = LineDiff.SplitLines(current);
		string[] baselineLines = LineDiff.SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty));
		var currentToActual = LineHunker.Hunks(currentLines, LineDiff.SplitLines(provenance.Text));
		foreach (var hunk in LineHunker.Hunks(currentLines, baselineLines)) {
			if (!ContainsRange(currentRange, hunk.BeforeRange) || !ContainsRange(baselineRange, hunk.AfterRange)) {
				continue;
			}
			if (Length(hunk.BeforeRange) == 0) {
				int gap = MapBoundary(hunk.BeforeRange.Start, currentToActual, endBias: false) - 1;
				if (provenance.DeletedAtGap.TryGetValue(gap, out var segments)) {
					foreach (var segment in segments.Where(segment => segment.Origin.Pending)) {
						edits.Add(new CorrectionEdit(
							Relativize(path),
							string.Empty,
							string.Join("\n", segment.Lines),
							segment.Origin.Prompt,
							segment.Origin.Id,
							Continuable: false));
					}
				}
				continue;
			}

			var origins = new List<AgentOrigin?>();
			for (int i = hunk.BeforeRange.Start; i < hunk.BeforeRange.EndExclusive; i++) {
				var actual = MapRange(new LineRange(i, i + 1), currentToActual);
				origins.Add(Length(actual) == 1 && provenance.Lines[actual.Start - 1] is { Pending: true } origin ? origin : null);
			}
			var common = origins[0];
			if (common is not null && origins.All(origin => origin?.Id == common.Id)) {
				edits.Add(new CorrectionEdit(
					Relativize(path),
					Slice(currentLines, hunk.BeforeRange),
					Slice(baselineLines, hunk.AfterRange),
					common.Prompt,
					common.Id,
					Continuable: false));
				continue;
			}

			int paired = Math.Min(Length(hunk.BeforeRange), Length(hunk.AfterRange));
			for (int i = 0; i < origins.Count; i++) {
				if (origins[i] is not { } origin) {
					continue;
				}
				var before = new LineRange(hunk.BeforeRange.Start + i, hunk.BeforeRange.Start + i + 1);
				var after = i < paired
					? new LineRange(hunk.AfterRange.Start + i, hunk.AfterRange.Start + i + 1)
					: new LineRange(hunk.AfterRange.EndExclusive, hunk.AfterRange.EndExclusive);
				edits.Add(new CorrectionEdit(Relativize(path), Slice(currentLines, before), Slice(baselineLines, after), origin.Prompt, origin.Id, Continuable: false));
			}
		}
		return edits;
	}

	private static bool ContainsRange(LineRange outer, LineRange inner) =>
		outer.Start <= inner.Start && inner.EndExclusive <= outer.EndExclusive;

	private static LineRange MapRange(LineRange range, IReadOnlyList<LineHunk> hunks) =>
		new(MapBoundary(range.Start, hunks, endBias: false), MapBoundary(range.EndExclusive, hunks, endBias: true));

	private static int MapBoundary(int boundary, IReadOnlyList<LineHunk> hunks, bool endBias) {
		int delta = 0;
		foreach (var hunk in hunks) {
			if (boundary < hunk.BeforeRange.Start) {
				break;
			}
			if (boundary > hunk.BeforeRange.EndExclusive) {
				delta += Length(hunk.AfterRange) - Length(hunk.BeforeRange);
				continue;
			}
			if (hunk.BeforeRange.Start == hunk.BeforeRange.EndExclusive) {
				return endBias ? hunk.AfterRange.Start : hunk.AfterRange.EndExclusive;
			}
			if (boundary == hunk.BeforeRange.Start) {
				return hunk.AfterRange.Start;
			}
			if (boundary == hunk.BeforeRange.EndExclusive) {
				return hunk.AfterRange.EndExclusive;
			}
			return endBias ? hunk.AfterRange.EndExclusive : hunk.AfterRange.Start;
		}
		return boundary + delta;
	}

	// <paramref name="actual"/> is in provenance.Text (== disk) space, which is how provenance.Lines is indexed.
	// Callers holding a _current-space range map it through MapCurrentRangeToActual first.
	private void SetPending(string path, LineRange actual, bool pending) {
		if (!_provenance.TryGetValue(path, out var provenance)) {
			return;
		}
		for (int i = actual.Start - 1; i < actual.EndExclusive - 1 && i < provenance.Lines.Count; i++) {
			if (provenance.Lines[i] is { } origin) {
				provenance.Lines[i] = origin with { Pending = pending };
			}
		}
		foreach (int gap in provenance.DeletedAtGap.Keys.ToList()) {
			if (actual.Start - 1 <= gap && gap <= actual.EndExclusive - 1) {
				provenance.DeletedAtGap[gap] = [.. provenance.DeletedAtGap[gap]
					.Select(segment => segment with { Origin = segment.Origin with { Pending = pending } })];
			}
		}
	}

	private LineRange MapCurrentRangeToActual(string path, LineRange currentRange) {
		if (!_provenance.TryGetValue(path, out var provenance)
			|| !_current.TryGetValue(path, out string? current)) {
			return currentRange;
		}
		return MapRange(currentRange, LineHunker.Hunks(LineDiff.SplitLines(current), LineDiff.SplitLines(provenance.Text)));
	}

	// The inverse of MapCurrentRangeToActual: a live-model (== provenance.Text / disk) range → its position in
	// _current (which omits the user's non-agent edits). Identity when the two coincide, so a clean review is untouched.
	private LineRange MapActualRangeToCurrent(string path, LineRange actualRange) {
		if (!_provenance.TryGetValue(path, out var provenance)
			|| !_current.TryGetValue(path, out string? current)) {
			return actualRange;
		}
		return MapRange(actualRange, LineHunker.Hunks(LineDiff.SplitLines(provenance.Text), LineDiff.SplitLines(current)));
	}

	private void SetAllPending(string path, bool pending) {
		if (!_provenance.TryGetValue(path, out var provenance)) {
			return;
		}
		for (int i = 0; i < provenance.Lines.Count; i++) {
			if (provenance.Lines[i] is { } origin) {
				provenance.Lines[i] = origin with { Pending = pending };
			}
		}
		foreach (int gap in provenance.DeletedAtGap.Keys.ToList()) {
			provenance.DeletedAtGap[gap] = [.. provenance.DeletedAtGap[gap]
				.Select(segment => segment with { Origin = segment.Origin with { Pending = pending } })];
		}
	}

	private void PurgeAcceptedProvenance() {
		foreach (var provenance in _provenance.Values) {
			for (int i = 0; i < provenance.Lines.Count; i++) {
				if (provenance.Lines[i] is { Pending: false }) {
					provenance.Lines[i] = null;
				}
			}
			foreach (int gap in provenance.DeletedAtGap.Keys.ToList()) {
				provenance.DeletedAtGap[gap].RemoveAll(segment => !segment.Origin.Pending);
				if (provenance.DeletedAtGap[gap].Count == 0) {
					provenance.DeletedAtGap.Remove(gap);
				}
			}
		}
	}

	private ProvenanceFile? CloneProvenance(string path) =>
		_provenance.TryGetValue(path, out var provenance) ? provenance.Clone() : null;

	private static bool ProvenanceEquals(ProvenanceFile? left, ProvenanceFile? right) =>
		left is null ? right is null : left.EqualsState(right);

	private void RestoreProvenance(string path, ProvenanceFile? provenance) {
		if (provenance is null) {
			_provenance.Remove(path);
		} else {
			_provenance[path] = provenance.Clone();
		}
	}

	private static void AddGap(Dictionary<int, List<DeletedSegment>> gaps, int gap, DeletedSegment segment) {
		if (!gaps.TryGetValue(gap, out var segments)) {
			segments = [];
			gaps[gap] = segments;
		}
		segments.Add(segment);
	}

	private static bool ContainsBoundary(LineRange range, int boundary) =>
		range.Start <= boundary && boundary <= range.EndExclusive;

	private static int Length(LineRange range) => range.EndExclusive - range.Start;

	private static string Slice(IReadOnlyList<string> lines, LineRange range) => string.Join("\n", Lines(lines, range));

	private static List<string> Lines(IReadOnlyList<string> lines, LineRange range) =>
		[.. lines.Skip(range.Start - 1).Take(Length(range))];

	private void RaiseCorrected(IReadOnlyList<CorrectionEdit> edits) {
		if (edits.Count > 0) {
			Corrected?.Invoke(edits);
		}
	}

	private sealed record AgentOrigin(string? Prompt, bool Pending, long Id);
	private sealed record DeletedSegment(AgentOrigin Origin, List<string> Lines);
	private sealed record SegmentMatch(DeletedSegment Segment, int DeletedStart, int InsertedStart, int Count);

	private sealed class ProvenanceFile {
		public required string Text { get; set; }
		public required List<AgentOrigin?> Lines { get; set; }
		public required Dictionary<int, List<DeletedSegment>> DeletedAtGap { get; set; }

		public static ProvenanceFile Empty(string text) => new() {
			Text = text,
			Lines = [.. Enumerable.Repeat<AgentOrigin?>(null, LineDiff.SplitLines(text).Length)],
			DeletedAtGap = [],
		};

		public ProvenanceFile Clone() => new() {
			Text = Text,
			Lines = [.. Lines],
			DeletedAtGap = DeletedAtGap.ToDictionary(
				pair => pair.Key,
				pair => pair.Value.Select(segment => segment with { Lines = [.. segment.Lines] }).ToList()),
		};

		public bool EqualsState(ProvenanceFile? other) => other is not null
			&& string.Equals(Text, other.Text, StringComparison.Ordinal)
			&& Lines.SequenceEqual(other.Lines)
			&& DeletedAtGap.Count == other.DeletedAtGap.Count
			&& DeletedAtGap.All(pair => other.DeletedAtGap.TryGetValue(pair.Key, out var values)
				&& pair.Value.Count == values.Count
				&& pair.Value.Zip(values).All(pair => pair.First.Origin == pair.Second.Origin
					&& pair.First.Lines.SequenceEqual(pair.Second.Lines)));
	}

	private sealed record AttributedChange(
		LineRange BeforeRange,
		LineRange AfterRange,
		List<string> AfterLines,
		AgentOrigin Origin);
	private sealed record ProjectedChange(LineRange Range, List<string> AfterLines);
	private sealed record PreparedReviewChange(string DiskContent, ProvenanceFile? Provenance);
}
