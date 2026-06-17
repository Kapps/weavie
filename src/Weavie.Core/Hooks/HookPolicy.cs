namespace Weavie.Core.Hooks;

/// <summary>
/// Decides what the bridge does with each hook event. Today it always returns
/// <see cref="HookDecision.PassThrough"/>: the bridge's job is to OBSERVE every mutating tool call (the
/// change-recording stream), while edits stay governed by the openDiff presenter policy and other tools by
/// Claude's own prompts. A future <c>bypassPermissions</c> mode plugs in here — returning
/// <see cref="HookDecision.Allow"/> for PreToolUse reintroduces Claude's bypass WITHOUT
/// <c>--dangerously-skip-permissions</c> (which would suppress openDiff and the change stream).
/// </summary>
public static class HookPolicy {
	/// <summary>Returns the bridge's verdict for <paramref name="request"/>.</summary>
	/// <param name="request">The observed hook event.</param>
	public static HookDecision Decide(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		return HookDecision.PassThrough;
	}
}
