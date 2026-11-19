using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Core.FileActivity;

public sealed partial class WorkspaceInvalidationWatcher {
	private void OnCreated(FileSystemEventArgs e) {
		RecordKnown(e.FullPath, FileInvalidationKind.Created);
		SignalRefresh(e.FullPath);
	}

	// A content write can't add or remove an inventoried path, so only an ignore-rule edit re-lists anything.
	private void OnChanged(FileSystemEventArgs e) {
		RecordKnown(e.FullPath, FileInvalidationKind.Changed);
		if (WorkspacePaths.IsIgnoreRuleFile(e.FullPath)) {
			SignalRefresh(e.FullPath);
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
		if (_inventory.LastSnapshot?.Files.Contains(WorkspacePaths.CanonicalFsPath(Path.GetFullPath(fullPath))) == true) {
			Record(fullPath, kind);
			return true;
		}

		return false;
	}

	private IReadOnlyList<string> KnownDescendants(string directory) => _inventory.LastSnapshot is { } snapshot
		? [.. WorkspaceInventory.Beneath(snapshot.Files, WorkspacePaths.CanonicalFsPath(Path.GetFullPath(directory)))]
		: [];

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
