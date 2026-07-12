using Weavie.Core.Changes;

namespace Weavie.Core.Corrections;

/// <summary>
/// Appends the tracker's <see cref="SessionChangeTracker.Corrected"/> batches to the workspace's shared
/// <see cref="CorrectionCorpus"/>: one corpus entry per user action (an editor save over an agent hunk, or a
/// revert), each corrected file stored as a compact <see cref="CorrectionDiff"/>. No reasoning happens here —
/// raw deltas in, raw deltas stored. Subscribe with <c>tracker.Corrected += recorder.Record</c>.
/// </summary>
public sealed class CorrectionRecorder {
	private readonly CorrectionCorpus _corpus;

	/// <summary>Creates a recorder appending into <paramref name="corpus"/>.</summary>
	/// <param name="corpus">The workspace's shared correction ring.</param>
	public CorrectionRecorder(CorrectionCorpus corpus) {
		ArgumentNullException.ThrowIfNull(corpus);
		_corpus = corpus;
	}

	/// <summary>
	/// Records one user action's corrections: each edit's before→after as a unified diff, dropping EOL-only
	/// deltas, attributed to the first edit's producing prompt. A batch that diffs to nothing appends nothing.
	/// </summary>
	/// <param name="edits">The corrections a single user action produced.</param>
	public void Record(IReadOnlyList<CorrectionEdit> edits) {
		ArgumentNullException.ThrowIfNull(edits);
		var files = new List<CorrectionFile>(edits.Count);
		foreach (var edit in edits) {
			string delta = CorrectionDiff.Unified(edit.Before, edit.After);
			// An EOL-only difference diffs to nothing — not a correction.
			if (delta.Length > 0) {
				files.Add(new CorrectionFile { Path = edit.RelativePath, Delta = delta });
			}
		}

		if (files.Count > 0) {
			_corpus.Append(new CorrectionRecord { Prompt = edits[0].Prompt, Files = files });
		}
	}
}
