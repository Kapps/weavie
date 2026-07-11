using System.Text.Json;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// Session-attention push: classifies every loaded session's status transitions (turn complete / needs input /
// failed) into `session-attention` messages the web presents as a sound + OS notification — never active-gated,
// a background session's ping is the whole point. See docs/specs/session-attention.md.
public sealed partial class HostCore {
	/// <summary>Subscribes a session's status transitions and pushes a <c>session-attention</c> for each attention-worthy one.</summary>
	private void WireAttention(HostSession session) {
		// The machine delivers Changed serially (its delivery gate), so this closure-tracked previous status
		// can't race its own handler.
		var previous = session.Status.Status;
		session.Status.Changed += status => {
			var prior = previous;
			previous = status;
			if (AttentionRules.Classify(prior, status) is { } kind) {
				_ui.Post(() => PostSessionAttention(session, kind));
			}
		};
	}

	private void PostSessionAttention(HostSession session, AttentionKind kind) {
		// The rail identity (slot id + label) is what the web names and focuses; a session with no slot yet
		// (mid-startup) has no chip to take the user to, and its boot transitions are non-events anyway.
		var slot = _sessions?.Slots.FirstOrDefault(s => ReferenceEquals(s.Session, session));
		if (slot is null) {
			return;
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "session-attention",
			slot = slot.Id,
			label = slot.Label,
			kind = KindName(kind),
			providerId = slot.AgentProviderId,
		}));
	}

	// Exhaustive on purpose (like StatusName): a new kind must be wired here, not fall through silently.
	private static string KindName(AttentionKind kind) => kind switch {
		AttentionKind.TurnComplete => "turnComplete",
		AttentionKind.NeedsInput => "needsInput",
		AttentionKind.Failed => "failed",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unhandled attention kind"),
	};
}
