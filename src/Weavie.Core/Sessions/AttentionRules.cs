namespace Weavie.Core.Sessions;

/// <summary>An attention-worthy session event, presented to the user as a sound and/or OS notification.</summary>
public enum AttentionKind {
	/// <summary>A turn finished; the agent is idle awaiting the user.</summary>
	TurnComplete,

	/// <summary>The agent is blocked on the user: a permission prompt or a waiting-for-input notice.</summary>
	NeedsInput,

	/// <summary>The agent's process crashed or crash-looped.</summary>
	Failed,
}

/// <summary>
/// Classifies <see cref="SessionStatus"/> transitions into attention events. Booting to Idle and settling
/// to Waiting (the session resumes itself) are deliberately not events. See docs/specs/session-attention.md.
/// </summary>
public static class AttentionRules {
	/// <summary>The attention event for the <paramref name="previous"/> → <paramref name="next"/> transition, or <c>null</c> when it warrants none.</summary>
	public static AttentionKind? Classify(SessionStatus previous, SessionStatus next) => next switch {
		SessionStatus.Idle when previous == SessionStatus.Working => AttentionKind.TurnComplete,
		SessionStatus.NeedsInput => AttentionKind.NeedsInput,
		SessionStatus.Error => AttentionKind.Failed,
		_ => null,
	};

	// Exhaustive on purpose (like StatusName): a new kind must be wired here, not fall through silently.
	/// <summary>The wire name for <paramref name="kind"/>: the <c>session-attention</c> kind and the prefs gates key.</summary>
	public static string WireName(AttentionKind kind) => kind switch {
		AttentionKind.TurnComplete => "turnComplete",
		AttentionKind.NeedsInput => "needsInput",
		AttentionKind.Failed => "failed",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unhandled attention kind"),
	};
}
