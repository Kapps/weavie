namespace Weavie.Core.Changes;

/// <summary>
/// One file's out-of-band correction: what the agent last wrote vs what the file holds now, after the user
/// reverted hunks or hand-edited it outside the hook stream.
/// </summary>
/// <param name="RelativePath">The file's workspace-root-relative path with <c>/</c> separators.</param>
/// <param name="AgentText">The file's content after the agent's last write (as last reconciled).</param>
/// <param name="FinalText">The file's on-disk content now (empty when the user deleted it).</param>
public sealed record CorrectionEdit(string RelativePath, string AgentText, string FinalText);

// The correction snapshot: what the agent last wrote per file, held so a user's out-of-band revert or
// hand-edit (invisible to the hook stream) can be diffed out at each turn boundary. See
// docs/specs/learn-from-corrections.md.
public sealed partial class SessionChangeTracker {
	// Refreshed on every RecordChange, advanced to disk at each DrainCorrections, and retained while the file
	// still has pending (unreviewed) changes — so a revert made many turns after the write is still captured
	// (the review model accumulates; see docs/specs/turn-review.md). Agent deletions drop the entry
	// (ReconcileDeletions); user reverts that delete a created file keep it, so the deletion records.
	private readonly Dictionary<string, string> _agentOutput = new(StringComparer.Ordinal);

	// The disk content frozen when keep-all accepted a file, so a hand-edit made AFTER the acceptance isn't
	// misread as a correction. Only keep-all needs this: keep/revert leave _current equal to the on-disk
	// accepted content (they read/write disk), so a settled file falls back to _current — but AcceptTurn
	// advances the review baseline from _current, which never sees hand-edits (they bypass the tracker), so
	// its accepted "final" must be captured from disk explicitly. See docs/specs/learn-from-corrections.md.
	private readonly Dictionary<string, string> _settledFinal = new(StringComparer.Ordinal);

	/// <summary>
	/// Diffs each agent-written file's snapshot against the content the user settled on and returns the
	/// out-of-band corrections since the last drain. Each snapshot then advances (a correction reports once)
	/// and is dropped once its file has no pending review changes left. Call at a turn boundary or before
	/// /learn reads the corpus.
	/// </summary>
	public IReadOnlyList<CorrectionEdit> DrainCorrections() {
		lock (_gate) {
			List<CorrectionEdit>? edits = null;
			foreach (var (path, agentText) in new List<KeyValuePair<string, string>>(_agentOutput)) {
				bool pending = !string.Equals(
					_reviewBaseline.GetValueOrDefault(path, string.Empty),
					_current.GetValueOrDefault(path, string.Empty),
					StringComparison.Ordinal);
				// A pending file's final is a fresh disk read — it captures hand-edits the tracker never sees.
				// A settled (kept/reverted) file's final is what the user actually settled on: disk frozen at
				// keep-all if we have it, else _current (which keep/revert already advanced to the accepted
				// on-disk content), so a hand-edit made after the acceptance is correctly ignored.
				string final;
				if (pending) {
					final = ReadOrEmpty(path);
				} else if (_settledFinal.TryGetValue(path, out string? settled)) {
					final = settled;
				} else {
					final = _current.GetValueOrDefault(path, string.Empty);
				}

				if (!string.Equals(agentText, final, StringComparison.Ordinal)) {
					(edits ??= []).Add(new CorrectionEdit(Relativize(path), agentText, final));
				}

				if (pending) {
					_agentOutput[path] = final;
				} else {
					_agentOutput.Remove(path);
					_settledFinal.Remove(path);
				}
			}

			return edits ?? [];
		}
	}

	// Keep-all accepts the on-disk state of every file; freeze that disk content as each tracked file's
	// correction "final" so a later hand-edit isn't misread as a correction (AcceptTurn's _current doesn't
	// reflect hand-edits). Caller holds _gate.
	private void FreezeSettledFinalForAccept() {
		foreach (string path in _agentOutput.Keys) {
			_settledFinal[path] = ReadOrEmpty(path);
		}
	}

	// The post-edit content just recorded IS the agent's output for this file; a fresh agent write reopens
	// the correction window, so any frozen accepted-final is stale. Caller holds _gate.
	private void SnapshotAgentOutput(string path) {
		_agentOutput[path] = _current.GetValueOrDefault(path, string.Empty);
		_settledFinal.Remove(path);
	}

	// A file the agent itself removed (rm/mv reconciled at PostToolUse) is agent action, not a user
	// correction — drop its snapshot so the deletion never records as one. Caller holds _gate.
	private void DropAgentOutput(string path) {
		_agentOutput.Remove(path);
		_settledFinal.Remove(path);
	}
}
