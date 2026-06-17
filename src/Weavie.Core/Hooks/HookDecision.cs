using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>What the bridge tells Claude to do with a <see cref="HookEventKind.PreToolUse"/> tool call.</summary>
public enum HookDecisionKind {
	/// <summary>No opinion — defer to Claude's normal flow (openDiff for edits, the terminal prompt for the rest).</summary>
	PassThrough,

	/// <summary>Allow the tool without prompting (a future bypass mode).</summary>
	Allow,

	/// <summary>Block the tool.</summary>
	Deny,
}

/// <summary>
/// The bridge's verdict on a hook event, plus its serialization to the <c>hookSpecificOutput</c> JSON Claude
/// reads from a hook's stdout. <see cref="HookDecisionKind.PassThrough"/> serializes to
/// <see langword="null"/> (empty stdout = "no decision" = Claude's normal flow).
/// </summary>
public sealed record HookDecision {
	/// <summary>The verdict.</summary>
	public required HookDecisionKind Kind { get; init; }

	/// <summary>A reason surfaced to Claude (for allow/deny); <see langword="null"/> for pass-through.</summary>
	public string? Reason { get; init; }

	/// <summary>Defer to Claude's normal flow.</summary>
	public static HookDecision PassThrough { get; } = new() { Kind = HookDecisionKind.PassThrough };

	/// <summary>Allow the tool, citing <paramref name="reason"/>.</summary>
	/// <param name="reason">Why it was allowed.</param>
	public static HookDecision Allow(string reason) => new() { Kind = HookDecisionKind.Allow, Reason = reason };

	/// <summary>Deny the tool, citing <paramref name="reason"/>.</summary>
	/// <param name="reason">Why it was blocked.</param>
	public static HookDecision Deny(string reason) => new() { Kind = HookDecisionKind.Deny, Reason = reason };

	/// <summary>
	/// Renders the <c>hookSpecificOutput</c> JSON Claude expects on a PreToolUse hook's stdout, or
	/// <see langword="null"/> for <see cref="HookDecisionKind.PassThrough"/> (and for non-PreToolUse events,
	/// which carry no permission decision) — in which case the relay writes nothing.
	/// </summary>
	/// <param name="evt">The event being decided.</param>
	public string? ToHookOutputJson(HookEventKind evt) {
		if (Kind == HookDecisionKind.PassThrough || evt != HookEventKind.PreToolUse) {
			return null;
		}

		string decision = Kind == HookDecisionKind.Allow ? "allow" : "deny";
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			writer.WriteStartObject("hookSpecificOutput");
			writer.WriteString("hookEventName", "PreToolUse");
			writer.WriteString("permissionDecision", decision);
			writer.WriteString("permissionDecisionReason", Reason ?? string.Empty);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}
}
