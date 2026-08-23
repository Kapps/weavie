namespace Weavie.Core.Changes;

/// <summary>The result of <see cref="SessionChangeTracker.ApplyRevision"/>.</summary>
public enum ReviseApplyOutcome {
	/// <summary>The file's lines no longer matched the guard text — nothing was written (a concurrent edit).</summary>
	GuardMismatch,

	/// <summary>The region was replaced and the file rewritten on disk.</summary>
	Applied,
}

public sealed partial class SessionChangeTracker {
	/// <summary>
	/// Replaces <paramref name="range"/> with <paramref name="replacement"/>, guarding the file's current lines
	/// against <paramref name="originalText"/> so a concurrent edit aborts the write instead of clobbering it.
	/// The review baseline and accepted anchor are left where they are, so the user reviews one hunk spanning the
	/// baseline to the revised text rather than the pre-revision text as a second change.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="range">The region's range in the current file (1-based, end-exclusive).</param>
	/// <param name="originalText">The exact text of <paramref name="range"/> when the region was captured.</param>
	/// <param name="replacement">The text to splice in its place.</param>
	public ReviseApplyOutcome ApplyRevision(string path, LineRange range, string originalText, string replacement) {
		path = NormalizePath(path);
		ArgumentNullException.ThrowIfNull(originalText);
		ArgumentNullException.ThrowIfNull(replacement);
		lock (_gate) {
			// A revision is offered on any selection, so the file may be one the agent never touched. Seed its
			// review state first, or the write would land with no baseline and no way to review or revert it.
			if (!_reviewBaseline.ContainsKey(path)) {
				CaptureBaseline(path);
			}

			if (TrySplice(path, range, originalText, SplitLines(replacement)) is not { } spliced) {
				return ReviseApplyOutcome.GuardMismatch;
			}

			var before = Capture(path, withDisk: true);
			_fileSystem.WriteAllText(path, ApplyReviewChange(path, spliced.CurrentRaw, spliced.NewContent));
			_current[path] = spliced.NewContent;
			Record(ReviewActionKind.Revise, touchesDisk: true, range.Start, [before], [path]);
			ReportCurrentState(path);
		}

		return ReviseApplyOutcome.Applied;
	}
}
