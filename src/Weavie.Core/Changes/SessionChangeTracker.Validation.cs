namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private static bool ValidPatch(ReviewPatch? patch, HashSet<string> paths) => patch is not null
		&& patch.Id > 0 && patch.Path is not null && paths.Contains(patch.Path) && Enum.IsDefined(patch.Part)
		&& patch.Range.Start >= 1 && patch.Range.EndExclusive >= patch.Range.Start
		&& patch.Before is not null && patch.After is not null
		&& patch.Before.All(line => line is not null) && patch.After.All(line => line is not null)
		&& (patch.BeforeExists || patch.Before.Length == 0) && (patch.AfterExists || patch.After.Length == 0)
		&& patch.BeforeEol is "\n" or "\r\n" && patch.AfterEol is "\n" or "\r\n"
		&& ValidOrigins(patch.BeforeOrigins, patch.Before.Length) && ValidOrigins(patch.AfterOrigins, patch.After.Length)
		&& patch.Boundaries is not null && patch.Boundaries.All(boundary => boundary is not null && boundary.PatchId > 0 && boundary.Offset >= 0);

	private static bool ValidOrigins(OriginSlice? origins, int length) => origins is null
		|| origins.Lines is not null && origins.Lines.Length <= length && ValidGaps(origins.Gaps, length);

	private static bool ValidGaps(Dictionary<int, List<DeletedSegment>>? gaps, int length) => gaps is not null
		&& gaps.All(pair => pair.Key >= 0 && pair.Key <= length && pair.Value is not null
			&& pair.Value.All(segment => segment is not null && segment.Origin is not null && segment.Lines is not null
				&& segment.Lines.All(line => line is not null)));
}
