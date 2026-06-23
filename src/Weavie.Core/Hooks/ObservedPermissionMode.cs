namespace Weavie.Core.Hooks;

/// <summary>
/// The latest permission mode Claude reported through its hook input (<c>permission_mode</c>) — Weavie only
/// observes it (Claude owns its edit mode via Shift+Tab), to tell whether edits are auto-applying. <c>volatile</c>
/// because it's written on the hook accept loop and read on the host UI thread. See
/// <c>docs/specs/permission-modes-and-change-tracking.md</c>.
/// </summary>
public sealed class ObservedPermissionMode {
	private volatile string _current = "default";

	/// <summary>The mode Claude last reported, or <c>default</c> before the first tool call carries one.</summary>
	public string Current => _current;

	/// <summary>
	/// Raised on the hook accept loop when the observed mode actually changes value (not every event), so the
	/// host can react to a Shift+Tab — e.g. tear down a stale blocking openDiff when Claude flips to auto-apply.
	/// </summary>
	public event Action? Changed;

	/// <summary>
	/// True when Claude auto-applies edits without a per-edit review (<c>acceptEdits</c>/<c>bypassPermissions</c>) —
	/// the condition under which the post-turn review navigator is the review surface.
	/// </summary>
	public bool AutoAppliesEdits => _current is "acceptEdits" or "bypassPermissions";

	/// <summary>Folds a hook event in: records its reported <c>permission_mode</c>, if it carries one (else a no-op).</summary>
	/// <param name="request">The observed hook event.</param>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrEmpty(request.PermissionMode) || request.PermissionMode == _current) {
			return;
		}

		_current = request.PermissionMode;
		Changed?.Invoke();
	}
}
