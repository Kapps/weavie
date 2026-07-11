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

		var records = _corrections.ReadAll();
		if (records.Count == 0) {
			return CommandResult.Failure(
				"No corrections recorded yet — after you revert or hand-edit something the agent wrote, run this again.");
		}

		if (_primarySession is not { } primary) {
			return CommandResult.Failure("No primary session to prefill the analysis into.");
		}

		string prompt = LearnPrompt.Compose(records);
		_ui.Post(() => primary.PrefillAgentPrompt(prompt));
		// Consume exactly what was read: a correction another session appends mid-/learn stays in the ring.
		// Clearing fires Changed → the suggestion re-evaluates and the corrections.learn card vanishes.
		_corrections.Clear(records.Count);
		return CommandResult.Success(
			$"Prefilled an analysis of {records.Count} correction(s) into the primary session — review it and press Enter.");
	}
}
