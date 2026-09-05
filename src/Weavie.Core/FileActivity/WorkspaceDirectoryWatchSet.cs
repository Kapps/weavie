using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

internal interface IWorkspaceDirectoryWatchSet : IDisposable {
	int Count { get; }

	/// <summary>Installs and drops watches to match <paramref name="directories"/>; true when the set changed.</summary>
	bool Reconcile(IReadOnlyList<string> directories);

	void EnsureWatching(string directory);
}

internal sealed class FileSystemWorkspaceDirectoryWatchSet : IWorkspaceDirectoryWatchSet {
	private readonly Func<string, FileSystemWatcher> _create;
	private readonly Action<FileSystemEventArgs> _created;
	private readonly Action<FileSystemEventArgs> _changed;
	private readonly Action<FileSystemEventArgs> _deleted;
	private readonly Action<string, string> _renamed;
	private readonly Action<Exception> _error;
	private readonly Dictionary<string, FileSystemWatcher> _watchers;
	private readonly Lock _gate = new();
	private bool _disposed;

	public FileSystemWorkspaceDirectoryWatchSet(
		Func<string, FileSystemWatcher> create,
		Action<FileSystemEventArgs> created,
		Action<FileSystemEventArgs> changed,
		Action<FileSystemEventArgs> deleted,
		Action<string, string> renamed,
		Action<Exception> error) {
		_create = create;
		_created = created;
		_changed = changed;
		_deleted = deleted;
		_renamed = renamed;
		_error = error;
		_watchers = new Dictionary<string, FileSystemWatcher>(PathIdentity.Comparer);
	}

	public int Count {
		get { lock (_gate) { return _watchers.Count; } }
	}

	public bool Reconcile(IReadOnlyList<string> directories) {
		var desired = directories.ToHashSet(PathIdentity.Comparer);
		lock (_gate) {
			if (_disposed) {
				return false;
			}

			bool changed = false;
			foreach (string path in _watchers.Keys.Where(path => !desired.Contains(path)).ToArray()) {
				_watchers.Remove(path, out var obsolete);
				obsolete!.EnableRaisingEvents = false;
				obsolete.Dispose();
				changed = true;
			}

			foreach (string path in desired) {
				if (!_watchers.ContainsKey(path)) {
					TryAdd(path);
					changed = true;
				}
			}

			return changed;
		}
	}

	public void EnsureWatching(string directory) {
		lock (_gate) {
			if (!_disposed && !_watchers.ContainsKey(directory)) {
				TryAdd(directory);
			}
		}
	}

	private void TryAdd(string path) {
		try {
			_watchers.Add(path, Create(path));
		} catch (DirectoryNotFoundException) {
		} catch (ArgumentException) when (!Directory.Exists(path)) {
		}
	}

	private FileSystemWatcher Create(string path) {
		var watcher = _create(path);
		try {
			watcher.IncludeSubdirectories = false;
			watcher.NotifyFilter = NotifyFilters.FileName
				| NotifyFilters.DirectoryName
				| NotifyFilters.LastWrite
				| NotifyFilters.CreationTime
				| NotifyFilters.Size;
			watcher.Created += (_, e) => _created(e);
			watcher.Changed += (_, e) => _changed(e);
			watcher.Deleted += (_, e) => _deleted(e);
			watcher.Renamed += (_, e) => _renamed(e.OldFullPath, e.FullPath);
			watcher.Error += (_, e) => _error(e.GetException());
			watcher.EnableRaisingEvents = true;
			return watcher;
		} catch {
			watcher.Dispose();
			throw;
		}
	}

	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			foreach (var watcher in _watchers.Values) {
				watcher.EnableRaisingEvents = false;
				watcher.Dispose();
			}

			_watchers.Clear();
		}
	}
}

internal sealed class RecursiveWorkspaceDirectoryWatchSet : IWorkspaceDirectoryWatchSet {
	private readonly string _root;
	private readonly Action<FileSystemEventArgs> _created;
	private readonly Action<FileSystemEventArgs> _changed;
	private readonly Action<FileSystemEventArgs> _deleted;
	private readonly Action<string, string> _renamed;
	private readonly Action<Exception> _error;
	private readonly Lock _gate = new();
	private FileSystemWatcher? _watcher;
	private bool _disposed;

	public RecursiveWorkspaceDirectoryWatchSet(
		string root,
		Action<FileSystemEventArgs> created,
		Action<FileSystemEventArgs> changed,
		Action<FileSystemEventArgs> deleted,
		Action<string, string> renamed,
		Action<Exception> error) {
		_root = root;
		_created = created;
		_changed = changed;
		_deleted = deleted;
		_renamed = renamed;
		_error = error;
	}

	public int Count {
		get { lock (_gate) { return _watcher is null ? 0 : 1; } }
	}

	public bool Reconcile(IReadOnlyList<string> directories) {
		bool watching = Count > 0;
		EnsureWatching(_root);
		return !watching && Count > 0;
	}

	public void EnsureWatching(string directory) {
		if (!Directory.Exists(_root)) {
			return;
		}

		lock (_gate) {
			if (_disposed || _watcher is not null) {
				return;
			}

			var watcher = new FileSystemWatcher(_root) {
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName
					| NotifyFilters.DirectoryName
					| NotifyFilters.LastWrite
					| NotifyFilters.CreationTime
					| NotifyFilters.Size,
			};
			watcher.Created += (_, e) => _created(e);
			watcher.Changed += (_, e) => _changed(e);
			watcher.Deleted += (_, e) => _deleted(e);
			watcher.Renamed += (_, e) => _renamed(e.OldFullPath, e.FullPath);
			watcher.Error += (_, e) => _error(e.GetException());
			try {
				watcher.EnableRaisingEvents = true;
				_watcher = watcher;
			} catch {
				watcher.Dispose();
				throw;
			}
		}
	}

	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			if (_watcher is not null) {
				_watcher.EnableRaisingEvents = false;
				_watcher.Dispose();
				_watcher = null;
			}
		}
	}
}
