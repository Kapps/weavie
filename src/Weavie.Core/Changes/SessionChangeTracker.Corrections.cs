namespace Weavie.Core.Changes;

/// <summary>
/// One file's correction: what the agent had at a segment (<paramref name="Before"/>) vs what the user left
/// there (<paramref name="After"/>), captured the moment the user saved a hand-edit over an agent hunk or
/// reverted one, and attributed to the prompt that produced the agent output. See
/// docs/specs/learn-from-corrections.md.
/// </summary>
/// <param name="RelativePath">The file's workspace-root-relative path with <c>/</c> separators.</param>
/// <param name="Before">The agent's text for the corrected segment (empty when the user added lines the agent hadn't).</param>
/// <param name="After">The user's text for it (empty when the user reverted/deleted the agent's lines).</param>
/// <param name="Prompt">The prompt whose turn produced the corrected output; <see langword="null"/> when none was reported (Codex).</param>
public sealed record CorrectionEdit(string RelativePath, string Before, string After, string? Prompt);

// Correction capture: a correction is emitted at the moment the user acts on the agent's output — an editor
// save (RecordHandEdit) over an agent hunk, or a review-UI revert — never reconstructed by diffing the tree at
// a turn boundary. Attribution rides _producingPrompt: the in-flight prompt is stamped onto each file when the
// agent edits it. See docs/specs/learn-from-corrections.md.
public sealed partial class SessionChangeTracker {
	// The current turn's prompt (set at each UserPromptSubmit), stamped onto _producingPrompt when the agent edits
	// a file — so a correction attributes to the turn that WROTE the corrected output, not the next boundary.
	private string? _currentPrompt;
	private readonly Dictionary<string, string?> _producingPrompt = new(StringComparer.Ordinal);

	/// <summary>
	/// Raised (outside the lock) with the corrections a single user action just produced — an editor save over an
	/// agent hunk, or a revert. Empty batches never fire, so a subscriber that just appends never sees a no-op.
	/// </summary>
	public event Action<IReadOnlyList<CorrectionEdit>>? Corrected;

	/// <summary>
	/// Records a user's editor save as a correction when it edits an agent hunk. Only the saved lines that overlap
	/// an agent change (review baseline → current) are folded into <c>current</c>, so the user's edits to
	/// agent-untouched regions — their ongoing coding, which autosave fires on repeatedly — never register and can
	/// never pollute a later gate. A no-op when the file isn't tracked, has no pending agent change, or the save
	/// touched nothing the agent wrote.
	/// </summary>
	/// <param name="path">Absolute path of the file the editor just saved.</param>
	/// <param name="content">The file's full new content as the editor wrote it.</param>
	public void RecordHandEdit(string path, string content) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(content);
		CorrectionEdit? edit = null;
		lock (_gate) {
			if (!_current.TryGetValue(path, out string? current)) {
				return; // untracked (a scratch buffer, or a file no agent touched) — nothing to correct
			}

			var currentLines = SplitLines(current);
			var agentRanges = LineHunker
				.Hunks(SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty)), currentLines)
				.Select(h => h.AfterRange)
				.ToList();
			if (agentRanges.Count == 0) {
				return; // no pending agent change (never edited this turn, or fully kept) — the correction window is closed
			}

			string corrected = SpliceAgentEdits(currentLines, SplitLines(content), agentRanges, current);
			edit = Correction(path, current, corrected);
			if (edit is null) {
				return; // the save changed nothing the agent wrote
			}

			_current[path] = corrected;
		}

		RaiseCorrected([edit]);
	}

	// Rebuilds `current` with the saved content applied ONLY where the user's edit overlaps an agent range,
	// keeping the agent's own lines everywhere else — so a hand-edit to an agent-untouched region contributes
	// nothing. eolSource carries the file's EOL convention onto the rejoined result. Caller holds _gate.
	private static string SpliceAgentEdits(List<string> currentLines, List<string> contentLines, List<LineRange> agentRanges, string eolSource) {
		var result = new List<string>(contentLines.Count);
		int copied = 0; // 0-based count of currentLines already emitted
		foreach (var hunk in LineHunker.Hunks(currentLines, contentLines)) {
			for (int i = copied; i < hunk.BeforeRange.Start - 1; i++) {
				result.Add(currentLines[i]);
			}

			if (agentRanges.Exists(range => EditsAgentRegion(range, hunk.BeforeRange))) {
				for (int j = hunk.AfterRange.Start - 1; j < hunk.AfterRange.EndExclusive - 1; j++) {
					result.Add(contentLines[j]);
				}
			} else {
				for (int i = hunk.BeforeRange.Start - 1; i < hunk.BeforeRange.EndExclusive - 1; i++) {
					result.Add(currentLines[i]);
				}
			}

			copied = hunk.BeforeRange.EndExclusive - 1;
		}

		for (int i = copied; i < currentLines.Count; i++) {
			result.Add(currentLines[i]);
		}

		return JoinLines(result, eolSource);
	}

	// Whether a user edit hunk actually edits an agent-written region. A replacement or deletion counts on any
	// overlap; a pure insertion (empty range) counts only when it lands STRICTLY BETWEEN agent lines — an
	// insertion at either edge is new content prepended/appended to the region (their own coding, which autosave
	// fires on repeatedly), not a correction of the agent's lines.
	private static bool EditsAgentRegion(LineRange agent, LineRange edit) =>
		edit.Start == edit.EndExclusive
			? agent.Start < edit.Start && edit.Start < agent.EndExclusive
			: agent.Start < edit.EndExclusive && edit.Start < agent.EndExclusive;

	// A correction for one file, or null when before == after (nothing changed). Reads the producing prompt, so
	// callers building an edit for a file they then Forget must call this first. Caller holds _gate.
	private CorrectionEdit? Correction(string path, string before, string after) =>
		string.Equals(before, after, StringComparison.Ordinal)
			? null
			: new CorrectionEdit(Relativize(path), before, after, _producingPrompt.GetValueOrDefault(path));

	// Fires Corrected off the lock (matching Changed) so a subscriber's corpus write never runs under _gate.
	private void RaiseCorrected(IReadOnlyList<CorrectionEdit> edits) {
		if (edits.Count > 0) {
			Corrected?.Invoke(edits);
		}
	}
}
