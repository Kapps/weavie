using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests.Sessions;

/// <summary>
/// The full <see cref="AttentionRules"/> transition matrix — especially the non-events: booting to Idle,
/// settling to Waiting (self-resuming), and answering a prompt (NeedsInput → Working) must stay silent.
/// </summary>
public sealed class AttentionRulesTests {
	[Theory]
	[InlineData(SessionStatus.Working, SessionStatus.Idle, AttentionKind.TurnComplete)]
	[InlineData(SessionStatus.Working, SessionStatus.NeedsInput, AttentionKind.NeedsInput)]
	[InlineData(SessionStatus.Idle, SessionStatus.NeedsInput, AttentionKind.NeedsInput)]
	[InlineData(SessionStatus.Starting, SessionStatus.NeedsInput, AttentionKind.NeedsInput)]
	[InlineData(SessionStatus.Working, SessionStatus.Error, AttentionKind.Failed)]
	[InlineData(SessionStatus.Idle, SessionStatus.Error, AttentionKind.Failed)]
	[InlineData(SessionStatus.Starting, SessionStatus.Error, AttentionKind.Failed)]
	[InlineData(SessionStatus.Waiting, SessionStatus.Error, AttentionKind.Failed)]
	public void AttentionTransitions_Classify(SessionStatus previous, SessionStatus next, AttentionKind expected) =>
		Assert.Equal(expected, AttentionRules.Classify(previous, next));

	[Theory]
	[InlineData(SessionStatus.Starting, SessionStatus.Idle)]   // claude booted — nothing finished
	[InlineData(SessionStatus.Starting, SessionStatus.Working)]
	[InlineData(SessionStatus.Working, SessionStatus.Waiting)] // self-resuming (pending wakeup) — not done
	[InlineData(SessionStatus.NeedsInput, SessionStatus.Working)] // the user answered — silence
	[InlineData(SessionStatus.NeedsInput, SessionStatus.Idle)] // interrupted turn settling — no completed turn
	[InlineData(SessionStatus.Waiting, SessionStatus.Working)] // the wakeup fired
	[InlineData(SessionStatus.Waiting, SessionStatus.Idle)]    // drain bookkeeping, not a fresh completion
	[InlineData(SessionStatus.Idle, SessionStatus.Working)]
	[InlineData(SessionStatus.Error, SessionStatus.Starting)]  // post-crash restart
	public void QuietTransitions_ClassifyAsNull(SessionStatus previous, SessionStatus next) =>
		Assert.Null(AttentionRules.Classify(previous, next));
}
