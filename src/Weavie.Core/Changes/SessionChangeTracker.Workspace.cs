using Weavie.Core.Workspaces;

namespace Weavie.Core.Changes;

/// <summary>Workspace-wide change observation for tools whose touched files are unknown before execution.</summary>
public sealed partial class SessionChangeTracker {
	private readonly Dictionary<string, HashSet<string>> _workspaceMutationBaselines = new(StringComparer.Ordinal);
	private HashSet<string>? _workspaceTurnBaseline;

	private void BeginWorkspaceTurn() {
		var files = WorkspaceFiles();
		lock (_gate) {
			_workspaceTurnBaseline = new HashSet<string>(files, StringComparer.Ordinal);
		}

		foreach (string path in files) {
			CaptureBaseline(path);
		}
	}

	private void EndWorkspaceTurn() {
		HashSet<string>? baseline;
		lock (_gate) {
			baseline = _workspaceTurnBaseline;
			_workspaceTurnBaseline = null;
		}
		if (baseline is null) {
			return;
		}

		var candidates = new HashSet<string>(WorkspaceFiles(), StringComparer.Ordinal);
		candidates.UnionWith(baseline);
		List<string>? deleted = null;
		foreach (string path in candidates) {
			if (WorkspaceFileChanged(path)) {
				RecordChange(path);
				if (!_fileSystem.FileExists(path)) {
					(deleted ??= []).Add(path);
				}
			}
		}

		if (deleted is not null) {
			foreach (string path in deleted) {
				FileDeleted?.Invoke(path);
			}
		}
	}

	private bool WorkspaceTurnActive() {
		lock (_gate) {
			return _workspaceTurnBaseline is not null;
		}
	}

	private void CaptureWorkspaceBaselines(string invocationId) {
		ArgumentException.ThrowIfNullOrEmpty(invocationId);
		var files = WorkspaceFiles();
		lock (_gate) {
			_workspaceMutationBaselines[invocationId] = new HashSet<string>(files, StringComparer.Ordinal);
		}

		foreach (string path in files) {
			CaptureBaseline(path);
		}
	}

	private void RecordWorkspaceChanges(string invocationId) {
		ArgumentException.ThrowIfNullOrEmpty(invocationId);
		foreach (string path in WorkspaceMutationCandidates(invocationId)) {
			if (WorkspaceFileChanged(path)) {
				RecordChange(path);
			}
		}

		lock (_gate) {
			_workspaceMutationBaselines.Remove(invocationId);
		}
	}

	private bool WorkspaceFileChanged(string path) {
		string current = ReadOrEmpty(path);
		lock (_gate) {
			if (!_reviewBaseline.TryGetValue(path, out string? baseline)) {
				return true;
			}

			if (!_current.TryGetValue(path, out string? known)) {
				return !string.Equals(baseline, current, StringComparison.Ordinal);
			}

			return !string.Equals(known, current, StringComparison.Ordinal);
		}
	}

	private IReadOnlyList<string> WorkspaceFiles() {
		if (!_fileSystem.DirectoryExists(_workspaceRoot)) {
			return [];
		}

		var files = new List<string>();
		var stack = new Stack<string>();
		stack.Push(_workspaceRoot);
		while (stack.Count > 0) {
			string current = stack.Pop();
			foreach (var entry in _fileSystem.EnumerateDirectory(current)) {
				string path = Path.Combine(current, entry.Name);
				if (entry.IsDirectory) {
					if (!WorkspacePaths.IsIgnoredSegment(entry.Name)) {
						stack.Push(path);
					}
				} else if (_isInScope(path)) {
					files.Add(path);
				}
			}
		}

		files.Sort(StringComparer.OrdinalIgnoreCase);
		return files;
	}

	private IReadOnlyList<string> WorkspaceMutationCandidates(string invocationId) {
		var candidates = new HashSet<string>(WorkspaceFiles(), StringComparer.Ordinal);
		lock (_gate) {
			if (_workspaceMutationBaselines.TryGetValue(invocationId, out var baseline)) {
				foreach (string path in baseline) {
					candidates.Add(path);
				}
			}
		}

		var files = candidates.ToList();
		files.Sort(StringComparer.OrdinalIgnoreCase);
		return files;
	}
}
