namespace Weavie.Core.Diffs;

public enum DiffResult
{
    Kept,
    Rejected,
}

/// <summary>
/// The result of resolving a <see cref="DiffProposal"/>. Maps onto the MCP <c>openDiff</c>
/// response: <see cref="DiffResult.Kept"/> -&gt; FILE_SAVED (+ the final, possibly user-edited
/// contents); <see cref="DiffResult.Rejected"/> -&gt; DIFF_REJECTED.
/// </summary>
public sealed record DiffOutcome
{
    private DiffOutcome(DiffResult result, string? finalContents)
    {
        Result = result;
        FinalContents = finalContents;
    }

    public DiffResult Result { get; }

    /// <summary>The contents written on Keep (after any in-diff editing); null when rejected.</summary>
    public string? FinalContents { get; }

    public static DiffOutcome Kept(string finalContents)
    {
        ArgumentNullException.ThrowIfNull(finalContents);
        return new DiffOutcome(DiffResult.Kept, finalContents);
    }

    public static DiffOutcome Rejected() => new(DiffResult.Rejected, finalContents: null);
}
