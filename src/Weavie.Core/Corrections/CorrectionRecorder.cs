using Weavie.Core.Changes;

namespace Weavie.Core.Corrections;

/// <summary>Appends action-time correction batches to a workspace's shared corpus.</summary>
public sealed class CorrectionRecorder {
	/// <summary>The cap on distinct in-flight regions — bounds a long session's memory, not user config.</summary>
	public const int MaxInFlightRegions = 200;

	private readonly CorrectionCorpus _corpus;
	private readonly Lock _gate = new();
	// Per agent region the correction it last stored, so successive editor saves that keep retyping over one region
	// supersede a single entry instead of recording every intermediate keystroke-save. One agent edit mints one
	// origin across all its hunks, so a region is identified within its origin by running-replacement continuity —
	// hence a list per origin, one live chain per region the user is editing. Session-scoped: origin ids are the
	// tracker's, and a burst of autosaves is one continuous session.
	private readonly Dictionary<(string Path, long OriginId), List<Chain>> _inFlight = [];
	// Insertion order of _inFlight's keys, so once the region count exceeds MaxInFlightRegions the oldest region is
	// dropped — most sessions never touch this cap (a chain is normally short-lived: it drops on retype-to-original),
	// but an abandoned in-progress region otherwise sits here, holding its full Before/After text, for the rest of
	// the session. Eviction just stops coalescing that region: its next save starts a fresh chain, same as any other
	// region the recorder hasn't seen before.
	private readonly Queue<(string Path, long OriginId)> _inFlightOrder = new();

	/// <summary>Creates a recorder appending into <paramref name="corpus"/>.</summary>
	/// <param name="corpus">The workspace's shared correction ring.</param>
	public CorrectionRecorder(CorrectionCorpus corpus) {
		ArgumentNullException.ThrowIfNull(corpus);
		_corpus = corpus;
	}

	/// <summary>Stores one user's corrections as unified diffs grouped by their producing prompt.</summary>
	/// <param name="edits">The corrections produced by one user action.</param>
	public void Record(IReadOnlyList<CorrectionEdit> edits) {
		ArgumentNullException.ThrowIfNull(edits);
		if (edits.Count == 0) {
			return;
		}

		// An editor save (autosave repeats it as the user types) coalesces per region; a discrete revert appends.
		lock (_gate) {
			if (edits[0].Continuable) {
				foreach (var edit in edits) {
					Coalesce(edit);
				}
			} else {
				AppendGrouped(edits);
			}
		}
	}

	// Coalesce iff this save continues a live chain: its new text picks up where that chain left off, over text
	// that still had content (a running replacement). A restore-from-empty or a deletion boundary matches nothing,
	// so it starts a fresh chain. The stored delta stays anchored at the region's original agent text, so the entry
	// reads agent-output → current however many keystroke-saves crossed it; retyping back to the agent's output
	// drops it entirely.
	private void Coalesce(CorrectionEdit edit) {
		var key = (edit.RelativePath, edit.OriginId);
		var chains = _inFlight.GetValueOrDefault(key);
		var chain = chains?.Find(c => c.After.Length > 0 && string.Equals(edit.Before, c.After, StringComparison.Ordinal));
		if (chain is not null) {
			string delta = CorrectionDiff.Unified(chain.Before, edit.After);
			if (delta.Length == 0) {
				_corpus.Remove(chain.Line);
				chains!.Remove(chain);
			} else {
				chain.After = edit.After;
				chain.Line = _corpus.Coalesce(OneFile(edit, delta), chain.Line);
			}

			return;
		}

		string fresh = CorrectionDiff.Unified(edit.Before, edit.After);
		if (fresh.Length == 0) {
			return;
		}

		if (chains is null) {
			_inFlight[key] = chains = [];
			_inFlightOrder.Enqueue(key);
			while (_inFlight.Count > MaxInFlightRegions && _inFlightOrder.TryDequeue(out var oldest)) {
				_inFlight.Remove(oldest);
			}
		}

		chains.Add(new Chain(edit.Before, edit.After, _corpus.Append(OneFile(edit, fresh))));
	}

	// A revert's files, grouped one record per producing prompt (a revert-all rejects many files at once).
	private void AppendGrouped(IReadOnlyList<CorrectionEdit> edits) {
		foreach (var group in edits.GroupBy(edit => edit.Prompt)) {
			var files = new List<CorrectionFile>();
			foreach (var edit in group) {
				string delta = CorrectionDiff.Unified(edit.Before, edit.After);
				if (delta.Length > 0) {
					files.Add(new CorrectionFile { Path = edit.RelativePath, Delta = delta });
				}
			}

			if (files.Count > 0) {
				_corpus.Append(new CorrectionRecord { Prompt = group.Key, Files = files });
			}
		}
	}

	private static CorrectionRecord OneFile(CorrectionEdit edit, string delta) =>
		new() { Prompt = edit.Prompt, Files = [new CorrectionFile { Path = edit.RelativePath, Delta = delta }] };

	// One region's live correction: the delta stays anchored at Before (the original agent text); After is the last
	// stored text, so the next save continues this chain iff its Before equals it; Line is the corpus entry to supersede.
	private sealed class Chain(string before, string after, string line) {
		public string Before { get; } = before;
		public string After { get; set; } = after;
		public string Line { get; set; } = line;
	}
}
