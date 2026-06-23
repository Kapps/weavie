using Weavie.Core.Diffs;
using Weavie.Core.Hooks;

namespace Weavie.Core.Mcp;

/// <summary>
/// Auto-keeps an <c>openDiff</c> when Claude's observed edit mode (<see cref="ObservedPermissionMode"/>) is
/// auto-applying (<c>acceptEdits</c>/<c>bypassPermissions</c>) — a blocking review would be wrong once the edit
/// has applied; otherwise delegates the per-edit Keep/Reject to the inner presenter.
/// See <c>docs/specs/permission-modes-and-change-tracking.md</c>.
/// </summary>
public sealed class PermissionModeDiffPresenter : IDiffPresenter {
	private readonly IDiffPresenter _inner;
	private readonly ObservedPermissionMode _mode;

	/// <summary>Wraps <paramref name="inner"/>, auto-keeping when <paramref name="mode"/> reports edits auto-apply.</summary>
	public PermissionModeDiffPresenter(IDiffPresenter inner, ObservedPermissionMode mode) {
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(mode);
		_inner = inner;
		_mode = mode;
	}

	/// <inheritdoc/>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(proposal);
		if (_mode.AutoAppliesEdits) {
			// Auto-keep: report the proposed contents as kept (still flows through openDiff to be recorded), with
			// no blocking review — Claude's own mode already accepted it.
			return Task.FromResult(DiffOutcome.Kept(proposal.NewFileContents));
		}

		return _inner.PresentDiffAsync(proposal, cancellationToken);
	}

	/// <inheritdoc/>
	public Task OpenFileAsync(string filePath, bool preview, CancellationToken cancellationToken) =>
		_inner.OpenFileAsync(filePath, preview, cancellationToken);

	/// <inheritdoc/>
	public Task CloseTabAsync(string filePath, CancellationToken cancellationToken) =>
		_inner.CloseTabAsync(filePath, cancellationToken);
}
