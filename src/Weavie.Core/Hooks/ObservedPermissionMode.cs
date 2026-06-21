namespace Weavie.Core.Hooks;

/// <summary>
/// The latest permission mode Claude reported through its hook input (<c>permission_mode</c>). Claude owns its
/// edit mode (the user cycles it with Shift+Tab; Weavie cannot set it), so Weavie only observes it here, folding
/// the stream in via <see cref="Observe"/>. Used to tell whether edits are auto-applying. A single reference
/// field written from the hook accept loop and read on the host UI thread, so it is <c>volatile</c>. See
/// <c>docs/specs/permission-modes-and-change-tracking.md</c>.
/// </summary>
public sealed class ObservedPermissionMode {
	private volatile string _current = "default";

	/// <summary>The mode Claude last reported, or <c>default</c> before the first tool call carries one.</summary>
	public string Current => _current;

	/// <summary>
	/// True when Claude is auto-applying edits without a per-edit review (<c>acceptEdits</c> or
	/// <c>bypassPermissions</c>), the condition under which the post-turn review navigator is the review surface.
	/// </summary>
	public bool AutoAppliesEdits => _current is "acceptEdits" or "bypassPermissions";

	/// <summary>Folds a hook event in: records its reported <c>permission_mode</c>, if it carries one (else a no-op).</summary>
	/// <param name="request">The observed hook event.</param>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		if (!string.IsNullOrEmpty(request.PermissionMode)) {
			_current = request.PermissionMode;
		}
	}
}
