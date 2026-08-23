using Weavie.Core.Workspaces;

namespace Weavie.Core.FileActivity;

public sealed partial class WorkspaceInvalidationWatcher {
	private void OnCreated(FileSystemEventArgs e) {
		if (!Volatile.Read(ref _isRepository)) {
			if (WorkspacePaths.HasIgnoredSegment(e.FullPath)) {
				return;
			}

			if (Directory.Exists(e.FullPath)) {
				_inventory.TrackNonRepositoryDirectory(e.FullPath);
				_directoryWatchers.EnsureWatching(e.FullPath);
			} else {
				_inventory.TrackNonRepositoryFile(e.FullPath);
				Record(e.FullPath, FileInvalidationKind.Created);
			}
		} else {
			RecordKnown(e.FullPath, FileInvalidationKind.Created);
		}

		SignalRefresh();
	}

	// A content write can't add or remove an inventoried path, so only an ignore-rule edit re-enumerates;
	// non-repository tracking signals through the inventory's own Changed event when it learns a path.
	private void OnChanged(FileSystemEventArgs e) {
		if (!Volatile.Read(ref _isRepository)) {
			if (Directory.Exists(e.FullPath) || WorkspacePaths.HasIgnoredSegment(e.FullPath)) {
				return;
			}

			_inventory.TrackNonRepositoryFile(e.FullPath);
			Record(e.FullPath, FileInvalidationKind.Changed);
			return;
		}

		RecordKnown(e.FullPath, FileInvalidationKind.Changed);
		if (WorkspacePaths.IsIgnoreRuleFile(e.FullPath)) {
			SignalRefresh();
		}
	}

	private void OnDeleted(FileSystemEventArgs e) {
		if (Volatile.Read(ref _isRepository)) {
			var descendants = KnownDescendants(e.FullPath);
			if (descendants.Count > 0) {
				foreach (string file in descendants) {
					Record(file, FileInvalidationKind.Deleted);
				}
			} else {
				RecordKnown(e.FullPath, FileInvalidationKind.Deleted);
			}
		} else if (_inventory.IsKnownNonRepositoryDirectory(e.FullPath)) {
			foreach (string file in _inventory.ForgetNonRepositoryTree(e.FullPath)) {
				Record(file, FileInvalidationKind.Deleted);
			}
		} else if (_inventory.ForgetNonRepositoryFile(e.FullPath)) {
			Record(e.FullPath, FileInvalidationKind.Deleted);
		}

		SignalRefresh();
	}

	private void OnRenamed(string oldPath, string newPath) {
		bool repository = Volatile.Read(ref _isRepository);
		if (repository) {
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

			SignalRefresh();
			return;
		}

		bool ignoredDestination = WorkspacePaths.HasIgnoredSegment(newPath);
		if (_inventory.IsKnownNonRepositoryDirectory(oldPath)) {
			if (ignoredDestination) {
				foreach (string file in _inventory.ForgetNonRepositoryTree(oldPath)) {
					Record(file, FileInvalidationKind.Deleted);
				}
			} else {
				foreach (var move in _inventory.MoveNonRepositoryTree(oldPath, newPath)) {
					Record(move.OldPath, FileInvalidationKind.Deleted);
					Record(move.NewPath, FileInvalidationKind.Created);
				}

				_inventory.TrackNonRepositoryDirectory(newPath);
				_directoryWatchers.EnsureWatching(newPath);
			}

			SignalRefresh();
			return;
		}

		if (_inventory.ForgetNonRepositoryFile(oldPath)) {
			Record(oldPath, FileInvalidationKind.Deleted);
		}

		if (!ignoredDestination && Directory.Exists(newPath)) {
			_inventory.TrackNonRepositoryDirectory(newPath);
			_directoryWatchers.EnsureWatching(newPath);
		} else if (!ignoredDestination) {
			_inventory.TrackNonRepositoryFile(newPath);
			Record(newPath, FileInvalidationKind.Created);
		}

		SignalRefresh();
	}

	private void OnError(Exception error) {
		Interlocked.CompareExchange(ref _watcherFailure, error, null);
		SignalRefresh();
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
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return [.. Volatile.Read(ref _files).Where(file => file.StartsWith(prefix, comparison))];
	}

	internal void Record(string fullPath, FileInvalidationKind kind) {
		string canonical = WorkspacePaths.CanonicalFsPath(Path.GetFullPath(fullPath));
		lock (_flushLock) {
			if (_disposed) {
				return;
			}

			_pending[canonical] = kind;
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
