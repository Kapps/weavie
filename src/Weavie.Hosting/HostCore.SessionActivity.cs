using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Each session's activity line for the switcher, coalesced: a burst of tool calls publishes only its latest
// line, at most once per interval, so a busy agent can't flood the bridge.
public sealed partial class HostCore {
	private static readonly TimeSpan ActivityPublishInterval = TimeSpan.FromMilliseconds(300);

	private void WireActivity(HostSession session) {
		int scheduled = 0;
		string? published = null;
		void Schedule() {
			if (Interlocked.Exchange(ref scheduled, 1) == 1) return;
			_ = session.Background.Run(async ct => {
				await Task.Delay(ActivityPublishInterval, _time, ct).ConfigureAwait(false);
				Volatile.Write(ref scheduled, 0);
				PostForSession(session, () => {
					string? line = session.Activity.Line;
					if (line == published) return;
					published = line;
					PostSessionActivity(session.Bus.BroadcastTarget, line);
				});
			});
		}

		session.Activity.Changed += Schedule;
		session.Status.Changed += _ => Schedule();
	}

	private static void PostSessionActivity(MessageTarget target, string? line) =>
		target.Feature("status").Publish("activity", WireJson.Default.SessionActivityChanged, new(line));
}
