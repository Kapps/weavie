using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Changes;

/// <summary>Workspace-wide change observation for tools whose touched files are unknown before execution.</summary>
public sealed partial class SessionChangeTracker {
	private readonly Dictionary<string, HashSet<string>> _workspaceMutationBaselines = new(StringComparer.Ordinal);
	private WorkspaceTurnSnapshot? _workspaceTurnBaseline;
	private sealed record WorkspaceTurnSnapshot(Dictionary<string, byte[]> Files, Dictionary<string, string> Known);

	private void BeginWorkspaceTurn() {
		var files = WorkspaceFiles();
		var contents = files.ToDictionary(path => path, ReadOrEmpty, StringComparer.Ordinal);
		lock (_gate) {
			_workspaceTurnBaseline = new WorkspaceTurnSnapshot(
				contents.ToDictionary(pair => pair.Key, pair => Fingerprint(pair.Value), StringComparer.Ordinal),
				new Dictionary<string, string>(_current, StringComparer.Ordinal));
			foreach (var (path, content) in contents) {
				CaptureBaselineLocked(path, content, existed: true);
			}
		}
	}

	private void EndWorkspaceTurn() {
		WorkspaceTurnSnapshot? baseline;
		lock (_gate) {
			baseline = _workspaceTurnBaseline;
			_workspaceTurnBaseline = null;
		}
		if (baseline is null) {
			return;
		}

		var candidates = new HashSet<string>(WorkspaceFiles(), StringComparer.Ordinal);
		candidates.UnionWith(baseline.Files.Keys);
		candidates.UnionWith(baseline.Known.Keys);
		List<string>? deleted = null;
		foreach (string path in candidates) {
			bool existed = baseline.Files.TryGetValue(path, out byte[]? before);
			bool exists = _fileSystem.FileExists(path);
			string current = exists ? ReadOrEmpty(path) : string.Empty;
			bool diskChanged = existed != exists || (exists && !before.AsSpan().SequenceEqual(Fingerprint(current)));
			bool knownBefore = baseline.Known.TryGetValue(path, out string? beforeKnown);
			bool knownNow;
			string? nowKnown;
			lock (_gate) {
				knownNow = _current.TryGetValue(path, out nowKnown);
			}
			bool trackerChanged = knownBefore != knownNow
				|| (knownNow && !string.Equals(beforeKnown, nowKnown, StringComparison.Ordinal));
			if (diskChanged || trackerChanged) {
				RecordChange(path);
				if (!exists) {
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

	private static byte[] Fingerprint(string content) => SHA256.HashData(MemoryMarshal.AsBytes(content.AsSpan()));

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
