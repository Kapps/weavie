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
	private readonly bool _recursive;
	private readonly Dictionary<string, FileSystemWatcher> _watchers;
	private readonly Lock _gate = new();
	private bool _disposed;

	public FileSystemWorkspaceDirectoryWatchSet(
		Func<string, FileSystemWatcher> create,
		Action<FileSystemEventArgs> created,
		Action<FileSystemEventArgs> changed,
		Action<FileSystemEventArgs> deleted,
		Action<string, string> renamed,
		Action<Exception> error,
		bool recursive) {
		_create = create;
		_created = created;
		_changed = changed;
		_deleted = deleted;
		_renamed = renamed;
		_error = error;
		_recursive = recursive;
		_watchers = new Dictionary<string, FileSystemWatcher>(PathIdentity.Comparer);
	}

	public int Count {
		get { lock (_gate) { return _watchers.Count; } }
	}

	public bool Reconcile(IReadOnlyList<string> directories) {
		var desired = new HashSet<string>(PathIdentity.Comparer);
		foreach (string path in directories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).OrderBy(path => path.Length)) {
			if (!desired.Any(root => Covers(root, path))) desired.Add(path);
		}
		lock (_gate) {
			if (_disposed) {
				return false;
			}

			bool changed = false;
			// Arm replacement roots before releasing descendant handles that prevent Windows ancestor renames.
			foreach (string path in desired) {
				if (!_watchers.ContainsKey(path)) changed |= TryAdd(path);
			}
			foreach (string path in _watchers.Keys.Where(path => !desired.Contains(path)).ToArray()) {
				_watchers.Remove(path, out var obsolete);
				obsolete!.EnableRaisingEvents = false;
				obsolete.Dispose();
				changed = true;
			}

			return changed;
		}
	}

	public void EnsureWatching(string directory) {
		lock (_gate) {
			if (!_disposed) Reconcile([.. _watchers.Keys, directory]);
		}
	}

	private bool Covers(string root, string path) {
		if (PathIdentity.Comparer.Equals(root, path)) return true;
		if (!_recursive || !PathBoundary.Contains(root, path, PathIdentity.Comparison)) return false;
		for (var directory = new DirectoryInfo(path); !PathIdentity.Comparer.Equals(directory.FullName, root); directory = directory.Parent!) {
			// Recursive native watches do not cross directory links; those need their own root.
			if (directory.LinkTarget is not null) return false;
		}
		return true;
	}

	private bool TryAdd(string path) {
		try {
			_watchers.Add(path, Create(path));
			return true;
		} catch (DirectoryNotFoundException) {
		} catch (ArgumentException) when (!Directory.Exists(path)) {
		}
		return false;
	}

	private FileSystemWatcher Create(string path) {
		var watcher = _create(path);
		try {
			watcher.IncludeSubdirectories = _recursive;
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
	private readonly string? _rootParent;
	private readonly string _rootName;
	private readonly Action<FileSystemEventArgs> _created;
	private readonly Action<FileSystemEventArgs> _changed;
	private readonly Action<FileSystemEventArgs> _deleted;
	private readonly Action<string, string> _renamed;
	private readonly Action<Exception> _error;
	private readonly Lock _gate = new();
	private FileSystemWatcher? _watcher;
	private FileSystemWatcher? _selfDeleteWatcher;
	private bool _disposed;

	public RecursiveWorkspaceDirectoryWatchSet(
		string root,
		Action<FileSystemEventArgs> created,
		Action<FileSystemEventArgs> changed,
		Action<FileSystemEventArgs> deleted,
		Action<string, string> renamed,
		Action<Exception> error) {
		_root = PathIdentity.Normalize(root);
		_rootParent = Path.GetDirectoryName(_root);
		_rootName = Path.GetFileName(_root);
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

			ArmSelfDeleteWatch();
		}
	}

	// A watcher rooted at the directory itself does not reliably report that exact directory's own deletion
	// (macOS FSEvents can drop the stream with no further event; Windows invalidates the handle) — so a
	// session whose worktree is removed out from under it can go undetected indefinitely. Watching the parent
	// for this one entry's removal covers that gap, mirroring LinuxWorkspaceDirectoryWatchSet's IN_DELETE_SELF.
	private void ArmSelfDeleteWatch() {
		if (_selfDeleteWatcher is not null || string.IsNullOrEmpty(_rootName) || _rootParent is null || !Directory.Exists(_rootParent)) {
			return;
		}

		var watcher = new FileSystemWatcher(_rootParent) {
			IncludeSubdirectories = false,
			NotifyFilter = NotifyFilters.DirectoryName,
			Filter = _rootName,
		};
		watcher.Deleted += (_, e) => _deleted(e);
		watcher.Renamed += (_, e) => {
			if (PathIdentity.Comparer.Equals(e.OldFullPath, _root)) {
				_deleted(new FileSystemEventArgs(WatcherChangeTypes.Deleted, _rootParent, _rootName));
			}
		};
		try {
			watcher.EnableRaisingEvents = true;
			_selfDeleteWatcher = watcher;
		} catch (Exception ex) {
			watcher.Dispose();
			_error(ex);
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
			if (_selfDeleteWatcher is not null) {
				_selfDeleteWatcher.EnableRaisingEvents = false;
				_selfDeleteWatcher.Dispose();
				_selfDeleteWatcher = null;
			}
		}
	}
}
