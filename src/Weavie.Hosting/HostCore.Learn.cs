using Weavie.Core.Commands;
using Weavie.Core.Corrections;

namespace Weavie.Hosting;

// /learn (weavie.learn.fromCorrections): assemble the workspace's recorded corrections into one analysis
// prompt, PREFILL it into the primary session's agent (never auto-submit — the user reviews and presses
// Enter), and consume the read entries. Weavie stores the signal; the model does all the reasoning.
// See docs/specs/learn-from-corrections.md.
public sealed partial class HostCore {
	private CommandResult RunLearn() {
		// A correction to the latest turn hasn't hit a boundary yet — pull it in before reading.
		foreach (var session in LoadedSessions()) {
			session.Corrections.FlushPending();
		}

		// Peek (Count) before consuming so an empty ring — or a missing primary — fails WITHOUT draining.
		if (_corrections.Count == 0) {
			return CommandResult.Failure(
				"No corrections recorded yet — after you revert or hand-edit something the agent wrote, run this again.");
		}

		if (_primarySession is not { } primary) {
			return CommandResult.Failure("No primary session to prefill the analysis into.");
		}

		// Atomic read+clear: a correction another session appends after this stays in the ring; one appended
		// before is returned here and analyzed — never silently evicted. Take fires Changed → the suggestion
		// re-evaluates and the corrections.learn card vanishes.
		var records = _corrections.Take();
		if (records.Count == 0) {
			return CommandResult.Failure(
				"No corrections recorded yet — after you revert or hand-edit something the agent wrote, run this again.");
		}

		string prompt = LearnPrompt.Compose(records);
		_ui.Post(() => primary.PrefillAgentPrompt(prompt));
		return CommandResult.Success(
			$"Prefilled an analysis of {records.Count} correction(s) into the primary session — review it and press Enter.");
	}
}
