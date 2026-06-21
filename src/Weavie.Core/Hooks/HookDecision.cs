using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Hooks;

/// <summary>What the bridge tells Claude to do with a <see cref="HookEventKind.PreToolUse"/> tool call.</summary>
public enum HookDecisionKind {
	/// <summary>No opinion — defer to Claude's normal flow (openDiff for edits, the terminal prompt for the rest).</summary>
	PassThrough,

	/// <summary>Allow the tool without prompting (the claude.allowAllTools bypass).</summary>
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

	/// <summary>
	/// An optional line of text Claude surfaces to the user in the TUI (the top-level <c>systemMessage</c>
	/// hook field, valid on any event). Weavie sets it on PostToolUse edits to a clickable <c>file:line</c>
	/// jump target. Independent of <see cref="Kind"/> — a pass-through decision can still carry one.
	/// </summary>
	public string? SystemMessage { get; init; }

	/// <summary>Defer to Claude's normal flow.</summary>
	public static HookDecision PassThrough { get; } = new() { Kind = HookDecisionKind.PassThrough };

	/// <summary>Allow the tool, citing <paramref name="reason"/>.</summary>
	/// <param name="reason">Why it was allowed.</param>
	public static HookDecision Allow(string reason) => new() { Kind = HookDecisionKind.Allow, Reason = reason };

	/// <summary>Deny the tool, citing <paramref name="reason"/>.</summary>
	/// <param name="reason">Why it was blocked.</param>
	public static HookDecision Deny(string reason) => new() { Kind = HookDecisionKind.Deny, Reason = reason };

	/// <summary>
	/// Renders the JSON Claude reads from the hook's stdout: a <c>hookSpecificOutput</c> permission block for
	/// an allow/deny on a PreToolUse event, and/or a top-level <c>systemMessage</c> when
	/// <see cref="SystemMessage"/> is set. Returns <see langword="null"/> when there is nothing to say (a
	/// pass-through with no message) — in which case the relay writes nothing and Claude takes its normal flow.
	/// </summary>
	/// <param name="evt">The event being decided.</param>
	public string? ToHookOutputJson(HookEventKind evt) {
		bool emitDecision = Kind != HookDecisionKind.PassThrough && evt is HookEventKind.PreToolUse or HookEventKind.PermissionRequest;
		bool emitMessage = !string.IsNullOrEmpty(SystemMessage);
		if (!emitDecision && !emitMessage) {
			return null;
		}

		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			if (emitMessage) {
				writer.WriteString("systemMessage", SystemMessage);
			}

			if (emitDecision) {
				string decision = Kind == HookDecisionKind.Allow ? "allow" : "deny";
				writer.WriteStartObject("hookSpecificOutput");
				if (evt == HookEventKind.PermissionRequest) {
					// PermissionRequest nests the verdict in a decision object: {decision:{behavior}}.
					writer.WriteString("hookEventName", "PermissionRequest");
					writer.WriteStartObject("decision");
					writer.WriteString("behavior", decision);
					writer.WriteEndObject();
				} else {
					// PreToolUse uses a flat permissionDecision + reason.
					writer.WriteString("hookEventName", "PreToolUse");
					writer.WriteString("permissionDecision", decision);
					writer.WriteString("permissionDecisionReason", Reason ?? string.Empty);
				}
				writer.WriteEndObject();
			}

			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}
}
