namespace Weavie.Core.Hooks;

/// <summary>
/// The latest permission mode Claude Code reported through its hook input (<c>permission_mode</c>). Claude
/// OWNS its edit mode — the user cycles <c>default</c>/<c>acceptEdits</c>/<c>plan</c> with Shift+Tab and Weavie
/// cannot set it programmatically — so Weavie only OBSERVES it here, folding the stream in via
/// <see cref="Observe"/>. This is the edit-handling axis; the orthogonal tool-permission axis is the
/// <c>claude.allowAllTools</c> setting the hook enforces. Used to tell whether edits are auto-applying (so
/// there's no per-edit openDiff review and the post-turn review surface applies). A single reference field,
/// written from the hook accept loop and read on the host UI thread — <c>volatile</c> makes that safe without
/// a lock. See <c>docs/specs/permission-modes-and-change-tracking.md</c>.
/// </summary>
public sealed class ObservedPermissionMode {
	private volatile string _current = "default";

	/// <summary>The mode Claude last reported, or <c>default</c> before the first tool call carries one.</summary>
	public string Current => _current;

	/// <summary>
	/// True when Claude is auto-applying edits without a per-edit review (<c>acceptEdits</c> or
	/// <c>bypassPermissions</c>) — the condition under which the post-turn review navigator + inline applied
	/// markers are the review surface.
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
