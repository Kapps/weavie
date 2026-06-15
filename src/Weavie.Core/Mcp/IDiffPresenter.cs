using Weavie.Core.Diffs;

namespace Weavie.Core.Mcp;

/// <summary>
/// Surfaces an inbound <c>openDiff</c> to the user and resolves it. In production this renders an
/// editable Monaco diff in the webview and awaits the user's Keep/Reject; in tests it is scripted.
/// <c>openDiff</c> is blocking — the MCP response is withheld until this completes.
/// </summary>
public interface IDiffPresenter
{
    /// <summary>
    /// Presents the proposed diff and resolves to the outcome. On <see cref="DiffResult.Kept"/>,
    /// <see cref="DiffOutcome.FinalContents"/> is the (possibly user-edited) content to persist.
    /// </summary>
    Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken);

    /// <summary>Reveals a file in the editor (the MCP <c>openFile</c> tool).</summary>
    Task OpenFileAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Scripted <see cref="IDiffPresenter"/> for tests: returns a preset outcome and records calls.</summary>
public sealed class FakeDiffPresenter : IDiffPresenter
{
    private readonly Func<DiffProposal, DiffOutcome> _decide;

    public FakeDiffPresenter(Func<DiffProposal, DiffOutcome> decide) => _decide = decide;

    /// <summary>Keeps every diff, saving the proposed contents unchanged.</summary>
    public static FakeDiffPresenter AlwaysKeep() =>
        new(p => DiffOutcome.Kept(p.NewFileContents));

    public static FakeDiffPresenter AlwaysReject() => new(_ => DiffOutcome.Rejected());

    public List<DiffProposal> Presented { get; } = [];

    public List<string> Opened { get; } = [];

    public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken)
    {
        Presented.Add(proposal);
        return Task.FromResult(_decide(proposal));
    }

    public Task OpenFileAsync(string filePath, CancellationToken cancellationToken)
    {
        Opened.Add(filePath);
        return Task.CompletedTask;
    }
}
