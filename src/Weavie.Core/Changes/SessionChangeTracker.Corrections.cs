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

	/// <summary>
	/// Diffs each agent-written file's snapshot against its on-disk content and returns the user's
	/// out-of-band corrections since the last drain. Each snapshot then advances to disk (a correction
	/// reports once) and is dropped once its file has no pending review changes left. Call at a turn
	/// boundary or before /learn reads the corpus.
	/// </summary>
	public IReadOnlyList<CorrectionEdit> DrainCorrections() {
		lock (_gate) {
			List<CorrectionEdit>? edits = null;
			foreach (var (path, agentText) in new List<KeyValuePair<string, string>>(_agentOutput)) {
				string final = ReadOrEmpty(path);
				if (!string.Equals(agentText, final, StringComparison.Ordinal)) {
					(edits ??= []).Add(new CorrectionEdit(Relativize(path), agentText, final));
				}

				bool pending = !string.Equals(
					_reviewBaseline.GetValueOrDefault(path, string.Empty),
					_current.GetValueOrDefault(path, string.Empty),
					StringComparison.Ordinal);
				if (pending) {
					_agentOutput[path] = final;
				} else {
					_agentOutput.Remove(path);
				}
			}

			return edits ?? [];
		}
	}

	// The post-edit content just recorded IS the agent's output for this file. Caller holds _gate.
	private void SnapshotAgentOutput(string path) =>
		_agentOutput[path] = _current.GetValueOrDefault(path, string.Empty);

	// A file the agent itself removed (rm/mv reconciled at PostToolUse) is agent action, not a user
	// correction — drop its snapshot so the deletion never records as one. Caller holds _gate.
	private void DropAgentOutput(string path) => _agentOutput.Remove(path);
}
