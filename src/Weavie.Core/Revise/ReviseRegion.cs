using Weavie.Core.Changes;

namespace Weavie.Core.Revise;

/// <summary>A region the caller wants revised, before the service mints its id.</summary>
public sealed record ReviseTarget {
	/// <summary>Absolute path of the file holding the region.</summary>
	public required string Path { get; init; }

	/// <summary>The region's range in the file (1-based, end-exclusive).</summary>
	public required LineRange Range { get; init; }

	/// <summary>The exact text of <see cref="Range"/> as the caller captured it.</summary>
	public required string OriginalText { get; init; }
}

/// <summary>One anchored region in flight, addressed by an id stable for its lifetime.</summary>
public sealed record ReviseRegion {
	/// <summary>The session-unique id the client addresses this region by.</summary>
	public required int Id { get; init; }

	/// <summary>Absolute path of the file holding the region.</summary>
	public required string Path { get; init; }

	/// <summary>The region's range in the file (1-based, end-exclusive).</summary>
	public required LineRange Range { get; init; }

	/// <summary>The exact text of <see cref="Range"/> when the region was captured; the write's guard.</summary>
	public required string OriginalText { get; init; }
}

/// <summary>How one region's revision ended.</summary>
public enum ReviseOutcome {
	/// <summary>The revision was written to disk.</summary>
	Applied,

	/// <summary>The model returned the original text, so nothing was written.</summary>
	Unchanged,

	/// <summary>Another revision of an overlapping region is already running.</summary>
	AlreadyInFlight,

	/// <summary>The model returned no usable entry for this region.</summary>
	NotProposed,

	/// <summary>The editor holding the file refused the write.</summary>
	Declined,

	/// <summary>The file no longer held the region's original text, so the write was abandoned.</summary>
	Changed,

	/// <summary>The query failed, so no region was revised.</summary>
	QueryFailed,

	/// <summary>Writing the file failed.</summary>
	WriteFailed,
}

/// <summary>One region's terminal state and the reason it reached it.</summary>
/// <param name="Region">The region this describes.</param>
/// <param name="Outcome">How the revision ended.</param>
/// <param name="Reason">A user-facing explanation, empty when the revision succeeded.</param>
public readonly record struct ReviseResult(ReviseRegion Region, ReviseOutcome Outcome, string Reason);
