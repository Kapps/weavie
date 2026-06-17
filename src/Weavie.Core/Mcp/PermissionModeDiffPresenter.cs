using Weavie.Core.Configuration;
using Weavie.Core.Diffs;

namespace Weavie.Core.Mcp;

/// <summary>
/// Applies the user's edit-permission policy on top of an inner <see cref="IDiffPresenter"/>, read
/// live from the settings store on every call so it toggles without a restart:
/// <list type="bullet">
/// <item><c>default</c> — delegate to the inner presenter: the blocking Keep/Reject review.</item>
/// <item><c>acceptEdits</c> — auto-keep immediately (no prompt), so edits apply without blocking.</item>
/// </list>
/// Claude itself stays in its own <c>default</c> permission mode in both cases, so <c>openDiff</c>
/// keeps firing for every edit — the policy lives here, not in a Claude launch flag (which would
/// disable openDiff). That preserved signal is what lets a recorded changes view be added later.
/// See <c>docs/specs/permission-modes-and-change-tracking.md</c>.
/// </summary>
public sealed class PermissionModeDiffPresenter : IDiffPresenter {
	private const string ModeKey = "claude.permissionMode";

	private readonly IDiffPresenter _inner;
	private readonly SettingsStore _settings;

	/// <summary>Wraps <paramref name="inner"/>, reading the permission mode live from <paramref name="settings"/>.</summary>
	public PermissionModeDiffPresenter(IDiffPresenter inner, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(settings);
		_inner = inner;
		_settings = settings;
	}

	/// <inheritdoc/>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(proposal);
		if (string.Equals(_settings.GetString(ModeKey), "acceptEdits", StringComparison.Ordinal)) {
			// Auto-keep: report the proposed contents as kept. openDiff returns them and Claude writes —
			// no blocking review, but the edit still flowed through openDiff so it can be recorded.
			return Task.FromResult(DiffOutcome.Kept(proposal.NewFileContents));
		}

		return _inner.PresentDiffAsync(proposal, cancellationToken);
	}

	/// <inheritdoc/>
	public Task OpenFileAsync(string filePath, CancellationToken cancellationToken) =>
		_inner.OpenFileAsync(filePath, cancellationToken);
}
