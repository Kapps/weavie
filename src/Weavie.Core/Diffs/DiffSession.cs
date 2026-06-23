using Weavie.Core.Documents;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Diffs;

/// <summary>
/// An open <c>openDiff</c> interaction: original on the left, an editable proposed document on the right.
/// <see cref="Keep"/> saves (FILE_SAVED); <see cref="Reject"/> discards. Resolves exactly once.
/// </summary>
public sealed class DiffSession {
	private DiffSession(DiffProposal proposal, string originalContents, IDocumentModel proposed) {
		Proposal = proposal;
		OriginalContents = originalContents;
		ProposedDocument = proposed;
	}

	/// <summary>
	/// Opens a session: reads the original (empty if the file doesn't exist yet) and seeds an editable
	/// proposed document with the new contents, bound to the target path.
	/// </summary>
	public static DiffSession Open(DiffProposal proposal, IFileSystem fileSystem, IDocumentModelFactory modelFactory) {
		ArgumentNullException.ThrowIfNull(proposal);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(modelFactory);

		string original = fileSystem.FileExists(proposal.OldFilePath)
			? fileSystem.ReadAllText(proposal.OldFilePath)
			: string.Empty;
		var proposed = modelFactory.Create(proposal.NewFilePath, proposal.NewFileContents);
		return new DiffSession(proposal, original, proposed);
	}

	/// <summary>The proposal this session was opened for.</summary>
	public DiffProposal Proposal { get; }

	/// <summary>The left/original side of the diff.</summary>
	public string OriginalContents { get; }

	/// <summary>The editable right/proposed side — the user can apply edits here before keeping.</summary>
	public IDocumentModel ProposedDocument { get; }

	/// <summary>True once the session has been kept or rejected; a session resolves exactly once.</summary>
	public bool IsResolved { get; private set; }

	/// <summary>Saves the (possibly user-edited) proposed contents and reports FILE_SAVED.</summary>
	public DiffOutcome Keep() {
		ThrowIfResolved();
		IsResolved = true;
		ProposedDocument.Save();
		return DiffOutcome.Kept(ProposedDocument.GetText());
	}

	/// <summary>Discards the proposal without writing; reports DIFF_REJECTED.</summary>
	public DiffOutcome Reject() {
		ThrowIfResolved();
		IsResolved = true;
		return DiffOutcome.Rejected();
	}

	private void ThrowIfResolved() {
		if (IsResolved) {
			throw new InvalidOperationException("This diff session has already been resolved.");
		}
	}
}
