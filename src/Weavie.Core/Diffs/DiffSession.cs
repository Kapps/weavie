using Weavie.Core.Documents;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Diffs;

/// <summary>
/// An open <c>openDiff</c> interaction: the original file on the left, an editable proposed
/// document on the right. The user may further edit the proposed document before resolving.
/// <see cref="Keep"/> saves and reports FILE_SAVED; <see cref="Reject"/> discards. A session
/// resolves exactly once — the blocking semantics Claude expects from <c>openDiff</c>.
/// </summary>
public sealed class DiffSession
{
    private readonly IDocumentModel _proposed;
    private bool _resolved;

    private DiffSession(DiffProposal proposal, string originalContents, IDocumentModel proposed)
    {
        Proposal = proposal;
        OriginalContents = originalContents;
        _proposed = proposed;
    }

    /// <summary>
    /// Opens a diff session for a proposal: reads the original from <paramref name="fileSystem"/>
    /// (empty if the file does not yet exist) and seeds an editable proposed document with the
    /// new contents, bound to the target path.
    /// </summary>
    public static DiffSession Open(DiffProposal proposal, IFileSystem fileSystem, IDocumentModelFactory modelFactory)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(modelFactory);

        var original = fileSystem.FileExists(proposal.OldFilePath)
            ? fileSystem.ReadAllText(proposal.OldFilePath)
            : string.Empty;
        var proposed = modelFactory.Create(proposal.NewFilePath, proposal.NewFileContents);
        return new DiffSession(proposal, original, proposed);
    }

    public DiffProposal Proposal { get; }

    /// <summary>The left/original side of the diff.</summary>
    public string OriginalContents { get; }

    /// <summary>The editable right/proposed side — the user can apply edits here before keeping.</summary>
    public IDocumentModel ProposedDocument => _proposed;

    public bool IsResolved => _resolved;

    /// <summary>Saves the (possibly user-edited) proposed contents and reports FILE_SAVED.</summary>
    public DiffOutcome Keep()
    {
        ThrowIfResolved();
        _resolved = true;
        _proposed.Save();
        return DiffOutcome.Kept(_proposed.GetText());
    }

    /// <summary>Discards the proposal without writing; reports DIFF_REJECTED.</summary>
    public DiffOutcome Reject()
    {
        ThrowIfResolved();
        _resolved = true;
        return DiffOutcome.Rejected();
    }

    private void ThrowIfResolved()
    {
        if (_resolved)
        {
            throw new InvalidOperationException("This diff session has already been resolved.");
        }
    }
}
