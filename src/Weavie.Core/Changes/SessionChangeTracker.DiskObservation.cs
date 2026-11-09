using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

// Disk changes reach the tracker from the session's file activity, for the paths they touch; review actions read only
// the files they write.
public sealed partial class SessionChangeTracker {
	// Paths each conversation's agent tools are editing now, with how many tools: the tool's completion records the
	// path, so a change seen meanwhile is its own.
	private readonly Dictionary<string, Dictionary<string, int>> _agentEditing = new(StringComparer.Ordinal);
	// Paths an editor save is writing now, with how many saves; the save captures the result itself.
	private readonly Dictionary<string, int> _saving = new(PathIdentity.Comparer);

	/// <summary>
	/// Brings the tracked files among <paramref name="paths"/> up to their disk content, as an editor save or an
	/// outside edit left them, and answers which of them changed. Untracked paths and files an agent tool is
	/// editing are ignored.
	/// </summary>
	public IReadOnlyList<string> ObserveDisk(IReadOnlyCollection<string> paths) {
		ArgumentNullException.ThrowIfNull(paths);
		lock (_gate) {
			string[] observed = [.. paths.Select(NormalizePath).Distinct(PathIdentity.Comparer)
				.Where(path => (_baseline.ContainsKey(path) || _nonText.ContainsKey(path)) && !AgentEditing(path) && !_saving.ContainsKey(path))];
			if (observed.Length == 0) return [];
			var before = observed.ToDictionary(path => path, DiskState, PathIdentity.Comparer);
			foreach (string path in observed) {
				if (_nonText.ContainsKey(path) && !_fileSystem.FileExists(path)) _nonText.Remove(path);
			}

			ReconcileReviewDisk(observed);
			string[] changed = [.. observed.Where(path => before[path] != DiskState(path))];
			if (changed.Length > 0) Checkpoint();
			return changed;
		}
	}

	private bool AgentEditing(string path) => _agentEditing.Values.Any(paths => paths.ContainsKey(path));

	private void BeginAgentEdit(string conversationId, string path) {
		lock (_gate) {
			var paths = _agentEditing.TryGetValue(conversationId, out var open) ? open : _agentEditing[conversationId] = new(PathIdentity.Comparer);
			paths[path] = paths.GetValueOrDefault(path) + 1;
		}
	}

	private void EndAgentEditLocked(string conversationId, string path) {
		if (!_agentEditing.TryGetValue(conversationId, out var paths) || !paths.TryGetValue(path, out int count)) return;
		if (count > 1) paths[path] = count - 1;
		else paths.Remove(path);
		if (paths.Count == 0) _agentEditing.Remove(conversationId);
	}

	// A finished turn's tools that never completed (a denied or interrupted edit) record whatever they left behind,
	// each released only as it is recorded.
	private void EndAgentTurn(string conversationId) {
		string[] unfinished;
		lock (_gate) {
			if (!_agentEditing.TryGetValue(conversationId, out var paths)) return;
			unfinished = [.. paths.Keys];
			foreach (string path in unfinished) paths[path] = 1;
		}

		foreach (string path in unfinished) RecordChange(path, conversationId);
	}

	private (string? Current, bool Missing, bool NonText) DiskState(string path) =>
		(_current.GetValueOrDefault(path), _missingCurrent.Contains(path), _nonText.ContainsKey(path));
}
