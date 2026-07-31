using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// Session attention: classifies every loaded session's status transitions (turn complete / needs input /
// failed) into owned events the web presents as a sound + OS notification — never selection-gated,
// a background session's ping is the whole point. See docs/specs/session-attention.md.
public sealed partial class HostCore {
	/// <summary>Subscribes a session's status transitions and publishes each attention-worthy event on its bus.</summary>
	private void WireAttention(HostSession session) {
		// The machine delivers Changed serially (its delivery gate), so this closure-tracked previous status
		// can't race its own handler.
		var previous = session.Status.Status;
		session.Status.Changed += status => {
			var prior = previous;
			previous = status;
			if (AttentionRules.Classify(prior, status) is { } kind) {
				PostForSession(session, () => PostSessionAttention(session, kind));
			}
		};
	}

	private void PostSessionAttention(HostSession session, AttentionKind kind) {
		session.Bus.Feature("attention").Publish("raised", new {
			label = session.DisplayLabel,
			kind = AttentionRules.WireName(kind),
		});
	}
}
