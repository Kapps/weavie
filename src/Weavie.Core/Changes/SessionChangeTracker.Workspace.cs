using Weavie.Core.Workspaces;

namespace Weavie.Core.Changes;

/// <summary>Workspace-wide change observation for tools whose touched files are unknown before execution.</summary>
public sealed partial class SessionChangeTracker {
	private readonly HashSet<string> _workspaceMutationBaseline = new(StringComparer.Ordinal);

	private void CaptureWorkspaceBaselines() {
		var files = WorkspaceFiles();
		lock (_gate) {
			_workspaceMutationBaseline.Clear();
			foreach (string path in files) {
				_workspaceMutationBaseline.Add(path);
			}
		}

		foreach (string path in files) {
			CaptureBaseline(path);
		}
	}

	private void RecordWorkspaceChanges() {
		foreach (string path in WorkspaceMutationCandidates()) {
			if (WorkspaceFileChanged(path)) {
				RecordChange(path);
			}
		}

		lock (_gate) {
			_workspaceMutationBaseline.Clear();
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

	private IReadOnlyList<string> WorkspaceMutationCandidates() {
		var candidates = new HashSet<string>(WorkspaceFiles(), StringComparer.Ordinal);
		lock (_gate) {
			foreach (string path in _workspaceMutationBaseline) {
				candidates.Add(path);
			}
		}

		var files = candidates.ToList();
		files.Sort(StringComparer.OrdinalIgnoreCase);
		return files;
	}
}
