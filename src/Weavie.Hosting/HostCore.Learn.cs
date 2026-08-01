using Weavie.Core.Commands;
using Weavie.Core.Corrections;

namespace Weavie.Hosting;

// /learn (weavie.learn.fromCorrections): assemble the workspace's recorded corrections into one analysis
// prompt, PREFILL it into the invoking session's agent (never auto-submit — the user reviews and presses
// Enter), and consume the read entries. Weavie stores the signal; the model does all the reasoning.
// See docs/specs/learn-from-corrections.md.
public sealed partial class HostCore {
	private CommandResult RunLearn(HostSession session) {
		// Peek (Count) before consuming so an empty ring fails WITHOUT draining.
		if (_corrections.Count == 0) {
			return CommandResult.Failure(
				"No corrections recorded yet — after you revert or hand-edit something the agent wrote, run this again.");
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
		_ui.Post(() => session.PrefillAgentPrompt(prompt));
		return CommandResult.Success(
			$"Prefilled an analysis of {records.Count} correction(s) — review it and press Enter.");
	}
}
