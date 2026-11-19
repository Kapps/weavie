using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Core.FileActivity;

public sealed partial class WorkspaceInvalidationWatcher {
	private void OnCreated(FileSystemEventArgs e) {
		RecordKnown(e.FullPath, FileInvalidationKind.Created);
		SignalRefresh(e.FullPath);
	}

	// A content write can't add or remove an inventoried path, so only an ignore-rule edit re-enumerates.
	private void OnChanged(FileSystemEventArgs e) {
		RecordKnown(e.FullPath, FileInvalidationKind.Changed);
		if (WorkspacePaths.IsIgnoreRuleFile(e.FullPath)) {
			SignalFullRefresh();
		}
	}

	private void OnDeleted(FileSystemEventArgs e) {
		var descendants = KnownDescendants(e.FullPath);
		if (descendants.Count > 0) {
			foreach (string file in descendants) {
				Record(file, FileInvalidationKind.Deleted);
			}
		} else {
			RecordKnown(e.FullPath, FileInvalidationKind.Deleted);
		}

		SignalRefresh(e.FullPath);
	}

	private void OnRenamed(string oldPath, string newPath) {
		var descendants = KnownDescendants(oldPath);
		if (descendants.Count > 0) {
			foreach (string oldFile in descendants) {
				string newFile = Path.Combine(newPath, Path.GetRelativePath(oldPath, oldFile));
				Record(oldFile, FileInvalidationKind.Deleted);
				Record(newFile, FileInvalidationKind.Created);
			}
		} else {
			RecordKnown(oldPath, FileInvalidationKind.Deleted);
			RecordKnown(newPath, FileInvalidationKind.Created);
		}

		SignalRefresh(oldPath);
		SignalRefresh(newPath);
	}

	private void OnError(Exception error) {
		Interlocked.CompareExchange(ref _watcherFailure, error, null);
		SignalFullRefresh();
	}

	private bool RecordKnown(string fullPath, FileInvalidationKind kind) {
		var files = Volatile.Read(ref _files);
		if (files.Contains(WorkspacePaths.CanonicalFsPath(Path.GetFullPath(fullPath)))) {
			Record(fullPath, kind);
			return true;
		}

		return false;
	}

	private IReadOnlyList<string> KnownDescendants(string directory) {
		string canonical = WorkspacePaths.CanonicalFsPath(Path.GetFullPath(directory));
		string prefix = canonical.EndsWith(Path.DirectorySeparatorChar)
			? canonical
			: canonical + Path.DirectorySeparatorChar;
		return [.. Volatile.Read(ref _files).Where(file => file.StartsWith(prefix, PathIdentity.Comparison))];
	}

	internal void Record(string fullPath, FileInvalidationKind kind) {
		string canonical = WorkspacePaths.CanonicalFsPath(Path.GetFullPath(fullPath));
		lock (_flushLock) {
			if (_disposed) {
				return;
			}

			if (kind != FileInvalidationKind.Changed || !_pending.ContainsKey(canonical)) {
				_pending[canonical] = kind;
			}
			_debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
		}
	}

	private void Flush() {
		lock (_flushLock) {
			if (_disposed) {
				return;
			}
			DeliverPendingLocked();
		}
	}
}
