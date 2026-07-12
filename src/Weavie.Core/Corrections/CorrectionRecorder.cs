using Weavie.Core.Changes;

namespace Weavie.Core.Corrections;

/// <summary>Appends action-time correction batches to a workspace's shared corpus.</summary>
public sealed class CorrectionRecorder {
	private readonly CorrectionCorpus _corpus;

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
}
