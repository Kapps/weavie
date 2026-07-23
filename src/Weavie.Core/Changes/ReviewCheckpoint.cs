namespace Weavie.Core.Changes;

internal sealed record ReviewCheckpoint {
	public required int Version { get; init; }
	public required ReviewIdentity? Review { get; init; }
	public required long ArmToken { get; init; }
	public required long ActiveReviewToken { get; init; }
	public required long NextOriginId { get; init; }
	public required List<ReviewFileCheckpoint> Files { get; init; }
	public required List<ReviewDiskGuardCheckpoint> Guards { get; init; }
}

internal sealed record ReviewDiskGuardCheckpoint {
	public required string Path { get; init; }
	public required bool DiskExists { get; init; }
	public required string DiskHash { get; init; }
}

internal sealed record ReviewFileCheckpoint {
	public required string Path { get; init; }
	public required bool DiskExists { get; init; }
	public required string DiskHash { get; init; }
	public required bool CreatedSinceBaseline { get; init; }
	public required ReviewTextCheckpoint Current { get; init; }
	public required ReviewTextCheckpoint ReviewBaseline { get; init; }
	public required ReviewTextCheckpoint AcceptedAnchor { get; init; }
	public required ReviewTextCheckpoint SessionBaseline { get; init; }
	public required ReviewProvenanceCheckpoint Provenance { get; init; }
}

internal sealed record ReviewTextCheckpoint {
	public required string Hash { get; init; }
	public required List<ReviewSplice> Splices { get; init; }
}

internal sealed record ReviewSplice {
	public required int Offset { get; init; }
	public required int DeleteLength { get; init; }
	public required string InsertText { get; init; }
}

internal sealed record ReviewProvenanceCheckpoint {
	public required List<ReviewOriginCheckpoint> Origins { get; init; }
	public required List<ReviewOriginRunCheckpoint> Runs { get; init; }
	public required List<ReviewDeletedGapCheckpoint> DeletedGaps { get; init; }
}

internal sealed record ReviewOriginCheckpoint {
	public required long Id { get; init; }
	public required bool Pending { get; init; }
	public required string? Prompt { get; init; }
}

internal sealed record ReviewOriginRunCheckpoint {
	public required int Start { get; init; }
	public required int Length { get; init; }
	public required int Origin { get; init; }
}

internal sealed record ReviewDeletedGapCheckpoint {
	public required int Gap { get; init; }
	public required List<ReviewDeletedSegmentCheckpoint> Segments { get; init; }
}

internal sealed record ReviewDeletedSegmentCheckpoint {
	public required int Origin { get; init; }
	public required List<string> Lines { get; init; }
}
