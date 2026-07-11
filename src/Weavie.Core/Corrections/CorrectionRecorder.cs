using Weavie.Core.Agents;
using Weavie.Core.Changes;

namespace Weavie.Core.Corrections;

/// <summary>
/// The per-session observer that turns tracker drains into corpus entries: at each turn boundary
/// (<see cref="AgentPromptSubmitted"/>) it drains the tracker's correction snapshot and appends any net
/// user edit over agent output to the shared per-workspace <see cref="CorrectionCorpus"/>, attributed to
/// the prompt that produced the corrected output (the boundary carries the NEXT turn's prompt, which is
/// held for the next drain). No reasoning happens here — raw deltas in, raw deltas stored.
/// </summary>
public sealed class CorrectionRecorder {
	private readonly SessionChangeTracker _changes;
	private readonly CorrectionCorpus _corpus;
	private readonly Lock _gate = new();
	private string? _pendingPrompt;

	/// <summary>Creates a recorder draining <paramref name="changes"/> into <paramref name="corpus"/>.</summary>
	/// <param name="changes">The session's change tracker, whose correction snapshot is drained at each boundary.</param>
	/// <param name="corpus">The workspace's shared correction ring.</param>
	public CorrectionRecorder(SessionChangeTracker changes, CorrectionCorpus corpus) {
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(corpus);
		_changes = changes;
		_corpus = corpus;
	}

	/// <summary>
	/// Folds an agent event into the recorder: a turn boundary records the pending corrections against the
	/// held prompt, then holds the boundary's own prompt for the next drain. All other events are ignored.
	/// </summary>
	/// <param name="value">The observed agent event.</param>
	public void Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		if (value is not AgentPromptSubmitted submitted) {
			return;
		}

		lock (_gate) {
			FlushLocked();
			_pendingPrompt = submitted.Prompt;
		}
	}

	/// <summary>
	/// Records any still-uncommitted correction now — /learn calls this on every loaded session before
	/// reading the corpus, so a correction to the latest turn isn't stranded waiting for the next prompt.
	/// </summary>
	public void FlushPending() {
		lock (_gate) {
			FlushLocked();
		}
	}

	private void FlushLocked() {
		var edits = _changes.DrainCorrections();
		if (edits.Count == 0) {
			return;
		}

		var files = new List<CorrectionFile>(edits.Count);
		foreach (var edit in edits) {
			string delta = CorrectionDiff.Unified(edit.AgentText, edit.FinalText);
			// An EOL-only difference diffs to nothing — not a correction.
			if (delta.Length > 0) {
				files.Add(new CorrectionFile { Path = edit.RelativePath, Delta = delta });
			}
		}

		if (files.Count > 0) {
			_corpus.Append(new CorrectionRecord { Prompt = _pendingPrompt, Files = files });
		}
	}
}
