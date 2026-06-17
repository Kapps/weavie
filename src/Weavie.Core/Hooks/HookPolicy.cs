namespace Weavie.Core.Hooks;

/// <summary>
/// Decides what the bridge does with each hook event, given the active <c>claude.permissionMode</c>:
/// <list type="bullet">
/// <item><c>default</c> / <c>acceptEdits</c> — <see cref="HookDecision.PassThrough"/>: defer to Claude's
/// normal flow (edits → the openDiff presenter policy; Bash → its own terminal prompt).</item>
/// <item><c>bypassPermissions</c> — <see cref="HookDecision.Allow"/> every PreToolUse call (Bash + edits):
/// Claude's bypass WITHOUT <c>--dangerously-skip-permissions</c> (which would suppress openDiff and the
/// change stream). The call is still observed/recorded by the bridge.</item>
/// </list>
/// Either way the bridge OBSERVES every mutating tool call (the change-recording stream).
/// </summary>
public static class HookPolicy {
	/// <summary>Returns the bridge's verdict for <paramref name="request"/> under <paramref name="permissionMode"/>.</summary>
	/// <param name="request">The observed hook event.</param>
	/// <param name="permissionMode">The active <c>claude.permissionMode</c> value.</param>
	public static HookDecision Decide(HookRequest request, string permissionMode) {
		ArgumentNullException.ThrowIfNull(request);
		if (request.Event == HookEventKind.PreToolUse
			&& string.Equals(permissionMode, "bypassPermissions", StringComparison.Ordinal)) {
			return HookDecision.Allow("Weavie bypassPermissions mode");
		}

		return HookDecision.PassThrough;
	}
}
