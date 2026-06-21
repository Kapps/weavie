using Weavie.Core.Diffs;
using Weavie.Core.Hooks;

namespace Weavie.Core.Mcp;

/// <summary>
/// Auto-keeps an <c>openDiff</c> when Claude is auto-applying edits, otherwise delegates to the inner
/// <see cref="IDiffPresenter"/> (the blocking Keep/Reject review). The signal is Claude's observed edit mode
/// (<see cref="ObservedPermissionMode"/>), which Claude owns (Shift+Tab) and Weavie reflects. In
/// <c>default</c> mode <c>openDiff</c> is the per-edit review; in <c>acceptEdits</c>/<c>bypassPermissions</c>
/// the edit already applied, so a blocking review would be wrong and the recorded change feed + post-turn
/// review are the surface instead. Claude stays free to call <c>openDiff</c> in any mode, so this guards
/// against it firing under an auto-apply mode. See <c>docs/specs/permission-modes-and-change-tracking.md</c>.
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
			// Auto-keep: report the proposed contents as kept. The edit still flows through openDiff so it can be
			// recorded, but there's no blocking review — Claude's own mode already accepted it.
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
