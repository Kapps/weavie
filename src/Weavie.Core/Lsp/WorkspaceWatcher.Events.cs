using Weavie.Core.Workspaces;

namespace Weavie.Core.Lsp;

public sealed partial class WorkspaceWatcher {
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
				Record(e.FullPath, FileChangeKind.Created);
			}
		} else {
			RecordKnown(e.FullPath, FileChangeKind.Created);
		}

		SignalRefresh();
	}

	private void OnChanged(FileSystemEventArgs e) {
		if (!Volatile.Read(ref _isRepository)) {
			if (Directory.Exists(e.FullPath) || WorkspacePaths.HasIgnoredSegment(e.FullPath)) {
				return;
			}

			_inventory.TrackNonRepositoryFile(e.FullPath);
			Record(e.FullPath, FileChangeKind.Changed);
		} else {
			RecordKnown(e.FullPath, FileChangeKind.Changed);
		}

		SignalRefresh();
	}

	private void OnDeleted(FileSystemEventArgs e) {
		if (Volatile.Read(ref _isRepository)) {
			var descendants = KnownDescendants(e.FullPath);
			if (descendants.Count > 0) {
				foreach (string file in descendants) {
					Record(file, FileChangeKind.Deleted);
				}
			} else {
				RecordKnown(e.FullPath, FileChangeKind.Deleted);
			}
		} else if (_inventory.IsKnownNonRepositoryDirectory(e.FullPath)) {
			foreach (string file in _inventory.ForgetNonRepositoryTree(e.FullPath)) {
				Record(file, FileChangeKind.Deleted);
			}
		} else if (_inventory.ForgetNonRepositoryFile(e.FullPath)) {
			Record(e.FullPath, FileChangeKind.Deleted);
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
					Record(oldFile, FileChangeKind.Deleted);
					Record(newFile, FileChangeKind.Created);
				}
			} else {
				RecordKnown(oldPath, FileChangeKind.Deleted);
				RecordKnown(newPath, FileChangeKind.Created);
			}

			SignalRefresh();
			return;
		}

		bool ignoredDestination = WorkspacePaths.HasIgnoredSegment(newPath);
		if (_inventory.IsKnownNonRepositoryDirectory(oldPath)) {
			if (ignoredDestination) {
				foreach (string file in _inventory.ForgetNonRepositoryTree(oldPath)) {
					Record(file, FileChangeKind.Deleted);
				}
			} else {
				foreach (var move in _inventory.MoveNonRepositoryTree(oldPath, newPath)) {
					Record(move.OldPath, FileChangeKind.Deleted);
					Record(move.NewPath, FileChangeKind.Created);
				}

				_inventory.TrackNonRepositoryDirectory(newPath);
				_directoryWatchers.EnsureWatching(newPath);
			}

			SignalRefresh();
			return;
		}

		if (_inventory.ForgetNonRepositoryFile(oldPath)) {
			Record(oldPath, FileChangeKind.Deleted);
		}

		if (!ignoredDestination && Directory.Exists(newPath)) {
			_inventory.TrackNonRepositoryDirectory(newPath);
			_directoryWatchers.EnsureWatching(newPath);
		} else if (!ignoredDestination) {
			_inventory.TrackNonRepositoryFile(newPath);
			Record(newPath, FileChangeKind.Created);
		}

		SignalRefresh();
	}

	private void OnError(Exception error) {
		Interlocked.CompareExchange(ref _watcherFailure, error, null);
		SignalRefresh();
	}

	private bool RecordKnown(string fullPath, FileChangeKind kind) {
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

	private void Record(string fullPath, FileChangeKind kind) {
		string ext = Path.GetExtension(fullPath);
		if (string.IsNullOrEmpty(ext) || !_extensions.Contains(ext)) {
			return;
		}

		_pending[fullPath] = kind;
		lock (_flushLock) {
			if (!_disposed) {
				_debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
			}
		}
	}

	private void Flush() {
		List<WatchedFileChange> batch;
		lock (_flushLock) {
			if (_pending.IsEmpty) {
				return;
			}

			batch = new List<WatchedFileChange>(_pending.Count);
			foreach (var (path, kind) in _pending) {
				if (_pending.TryRemove(path, out _)) {
					batch.Add(new WatchedFileChange(ToFileUri(path), kind));
				}
			}
		}

		if (batch.Count > 0) {
			_onChanges(batch);
		}
	}

	private static string ToFileUri(string fullPath) {
		try {
			return new Uri(fullPath).AbsoluteUri;
		} catch (UriFormatException) {
			return fullPath;
		}
	}
}
