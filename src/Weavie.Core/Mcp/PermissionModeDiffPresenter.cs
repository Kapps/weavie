using Weavie.Core.Configuration;
using Weavie.Core.Diffs;

namespace Weavie.Core.Mcp;

/// <summary>
/// Applies the user's edit-permission policy on top of an inner <see cref="IDiffPresenter"/>, read
/// live from the settings store on every call so it toggles without a restart:
/// <list type="bullet">
/// <item><c>default</c> — delegate to the inner presenter: the blocking Keep/Reject review.</item>
/// <item><c>acceptEdits</c> — auto-keep immediately (no prompt), so edits apply without blocking.</item>
/// <item><c>bypassPermissions</c> — also auto-keep (the hook bridge auto-allows tools; if Claude still
/// asks for an edit via openDiff, keep it without prompting).</item>
/// </list>
/// Claude itself stays in its own <c>default</c> permission mode in both cases, so <c>openDiff</c>
/// keeps firing for every edit — the policy lives here, not in a Claude launch flag (which would
/// disable openDiff). That preserved signal is what lets a recorded changes view be added later.
/// See <c>docs/specs/permission-modes-and-change-tracking.md</c>.
/// </summary>
public sealed class PermissionModeDiffPresenter : IDiffPresenter {
	/// <summary>The settings key for Claude's edit-permission mode (<c>default</c>/<c>acceptEdits</c>/<c>bypassPermissions</c>).</summary>
	public const string ModeKey = "claude.permissionMode";

	private readonly IDiffPresenter _inner;
	private readonly SettingsStore _settings;

	/// <summary>Wraps <paramref name="inner"/>, reading the permission mode live from <paramref name="settings"/>.</summary>
	public PermissionModeDiffPresenter(IDiffPresenter inner, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(settings);
		_inner = inner;
		_settings = settings;
	}

	/// <summary>
	/// True when the current mode auto-keeps edits (<c>acceptEdits</c>/<c>bypassPermissions</c>) — i.e. there is
	/// NO blocking openDiff review. The hosts use this to decide whether to surface the inline applied
	/// turn-markers: they are the review surface only when edits auto-apply; in <c>default</c> mode openDiff is
	/// the per-edit review, so a second applied marker would just demand a redundant Accept.
	/// </summary>
	/// <param name="settings">The settings store to read the mode from.</param>
	public static bool AutoKeepsEdits(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.GetString(ModeKey) is "acceptEdits" or "bypassPermissions";
	}

	/// <inheritdoc/>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(proposal);
		if (AutoKeepsEdits(_settings)) {
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
