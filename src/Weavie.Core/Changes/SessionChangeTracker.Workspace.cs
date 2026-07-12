using Weavie.Core.Workspaces;

namespace Weavie.Core.Changes;

/// <summary>Workspace-wide change observation for tools whose touched files are unknown before execution.</summary>
public sealed partial class SessionChangeTracker {
	private readonly Dictionary<string, HashSet<string>> _workspaceMutationBaselines = new(StringComparer.Ordinal);

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
		if (!_fileSystem.FileExists(path)) {
			lock (_gate) {
				return _reviewBaseline.ContainsKey(path); // a tracked text deletion reviews against empty content
			}
		}

		if (!TryReadOrEmpty(path, out string current)) {
			if (!_fileSystem.TryGetStat(path, out var stat)) {
				return false;
			}

			lock (_gate) {
				return !_nonText.TryGetValue(path, out var known)
					|| stat.MtimeMs != known.MtimeMs
					|| stat.Size != known.Size;
			}
		}

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
