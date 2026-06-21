namespace Weavie.Core.Diffs;

/// <summary>How a <see cref="DiffProposal"/> was resolved by the user.</summary>
public enum DiffResult {
	/// <summary>The proposal was accepted and its (possibly edited) contents saved.</summary>
	Kept,
	/// <summary>The proposal was discarded without writing anything.</summary>
	Rejected,
}

/// <summary>
/// The result of resolving a <see cref="DiffProposal"/>, mapping onto the MCP <c>openDiff</c> response:
/// <see cref="DiffResult.Kept"/> -&gt; FILE_SAVED (+ final contents); <see cref="DiffResult.Rejected"/>
/// -&gt; DIFF_REJECTED.
/// </summary>
public sealed record DiffOutcome {
	private DiffOutcome(DiffResult result, string? finalContents) {
		Result = result;
		FinalContents = finalContents;
	}

	/// <summary>Whether the diff was kept or rejected.</summary>
	public DiffResult Result { get; }

	/// <summary>The contents written on Keep (after any in-diff editing); null when rejected.</summary>
	public string? FinalContents { get; }

	/// <summary>Builds a Kept outcome carrying the final contents written to disk.</summary>
	public static DiffOutcome Kept(string finalContents) {
		ArgumentNullException.ThrowIfNull(finalContents);
		return new DiffOutcome(DiffResult.Kept, finalContents);
	}

	/// <summary>Builds a Rejected outcome with no final contents.</summary>
	public static DiffOutcome Rejected() => new(DiffResult.Rejected, finalContents: null);
}
