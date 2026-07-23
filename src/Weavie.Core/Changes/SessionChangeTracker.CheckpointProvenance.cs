namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private static ReviewProvenanceCheckpoint EncodeProvenance(ProvenanceFile? provenance) {
		if (provenance is null) {
			return new ReviewProvenanceCheckpoint { Origins = [], Runs = [], DeletedGaps = [] };
		}

		var origins = new List<AgentOrigin>();
		int OriginIndex(AgentOrigin origin) {
			int index = origins.IndexOf(origin);
			if (index >= 0) {
				return index;
			}
			origins.Add(origin);
			return origins.Count - 1;
		}

		var runs = new List<ReviewOriginRunCheckpoint>();
		for (int index = 0; index < provenance.Lines.Count;) {
			if (provenance.Lines[index] is not { } origin) {
				index++;
				continue;
			}
			int end = index + 1;
			while (end < provenance.Lines.Count && provenance.Lines[end] == origin) {
				end++;
			}
			runs.Add(new ReviewOriginRunCheckpoint { Start = index, Length = end - index, Origin = OriginIndex(origin) });
			index = end;
		}

		var gaps = provenance.DeletedAtGap
			.OrderBy(pair => pair.Key)
			.Select(pair => new ReviewDeletedGapCheckpoint {
				Gap = pair.Key,
				Segments = [.. pair.Value.Select(segment => new ReviewDeletedSegmentCheckpoint {
					Origin = OriginIndex(segment.Origin),
					Lines = [.. segment.Lines],
				})],
			})
			.ToList();
		return new ReviewProvenanceCheckpoint {
			Origins = [.. origins.Select(origin => new ReviewOriginCheckpoint {
				Id = origin.Id,
				Pending = origin.Pending,
				Prompt = origin.Prompt,
			})],
			Runs = runs,
			DeletedGaps = gaps,
		};
	}

	private static ProvenanceFile DecodeProvenance(
		string disk,
		ReviewProvenanceCheckpoint checkpoint,
		ref long greatestOrigin) {
		var origins = checkpoint.Origins
			.Select(value => new AgentOrigin(value.Prompt, value.Pending, value.Id))
			.ToList();
		foreach (var origin in origins) {
			greatestOrigin = Math.Max(greatestOrigin, origin.Id);
		}

		var provenance = ProvenanceFile.Empty(disk);
		foreach (var run in checkpoint.Runs) {
			if (run.Origin < 0
				|| run.Origin >= origins.Count
				|| run.Start < 0
				|| run.Start > provenance.Lines.Count
				|| run.Length <= 0
				|| run.Length > provenance.Lines.Count - run.Start) {
				throw new InvalidDataException("Review checkpoint contains an invalid provenance span.");
			}
			for (int index = run.Start; index < run.Start + run.Length; index++) {
				if (provenance.Lines[index] is not null) {
					throw new InvalidDataException("Review checkpoint contains overlapping provenance spans.");
				}
				provenance.Lines[index] = origins[run.Origin];
			}
		}

		foreach (var gap in checkpoint.DeletedGaps) {
			if (gap.Gap < 0 || gap.Gap > provenance.Lines.Count || provenance.DeletedAtGap.ContainsKey(gap.Gap)) {
				throw new InvalidDataException("Review checkpoint contains an invalid provenance gap.");
			}
			var segments = new List<DeletedSegment>(gap.Segments.Count);
			foreach (var segment in gap.Segments) {
				if (segment.Origin < 0 || segment.Origin >= origins.Count) {
					throw new InvalidDataException("Review checkpoint contains an invalid provenance origin.");
				}
				segments.Add(new DeletedSegment(origins[segment.Origin], [.. segment.Lines]));
			}
			provenance.DeletedAtGap[gap.Gap] = segments;
		}

		return provenance;
	}
}
