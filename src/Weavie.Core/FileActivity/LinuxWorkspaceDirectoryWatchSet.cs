using System.Runtime.InteropServices;

namespace Weavie.Core.FileActivity;

internal sealed partial class LinuxWorkspaceDirectoryWatchSet : IWorkspaceDirectoryWatchSet {
	// inotify may enqueue the two halves of a rename across reads; unmatched halves expire as deletes.
	private const int MovePairTimeoutMilliseconds = 100;
	private readonly Action<FileSystemEventArgs> _created;
	private readonly Action<FileSystemEventArgs> _changed;
	private readonly Action<FileSystemEventArgs> _deleted;
	private readonly Action<string, string> _renamed;
	private readonly Action<Exception> _error;
	private readonly Dictionary<string, int> _pathWatches = new(StringComparer.Ordinal);
	private readonly Dictionary<int, string> _watchPaths = [];
	private readonly Dictionary<uint, (string Path, long Deadline)> _pendingMoves = [];
	private readonly Lock _gate = new();
	private int _inotifyFd = -1;
	private int _stopFd = -1;
	private Thread? _reader;
	private bool _disposed;

	public LinuxWorkspaceDirectoryWatchSet(
		Action<FileSystemEventArgs> created,
		Action<FileSystemEventArgs> changed,
		Action<FileSystemEventArgs> deleted,
		Action<string, string> renamed,
		Action<Exception> error) {
		_created = created;
		_changed = changed;
		_deleted = deleted;
		_renamed = renamed;
		_error = error;
	}

	public int Count {
		get { lock (_gate) { return _pathWatches.Count; } }
	}

	public void Reconcile(IReadOnlyList<string> directories) {
		var desired = directories.ToHashSet(StringComparer.Ordinal);
		lock (_gate) {
			if (_disposed) {
				return;
			}

			EnsureStarted();
			foreach (string path in _pathWatches.Keys.Where(path => !desired.Contains(path)).ToArray()) {
				Remove(path);
			}

			foreach (string path in desired) {
				if (!_pathWatches.ContainsKey(path)) {
					Add(path);
				}
			}
		}
	}

	public void EnsureWatching(string directory) {
		lock (_gate) {
			if (_disposed || _pathWatches.ContainsKey(directory)) {
				return;
			}

			EnsureStarted();
			Add(directory);
		}
	}

	private void EnsureStarted() {
		if (_inotifyFd >= 0) {
			return;
		}

		_inotifyFd = inotify_init1(CloseOnExec | NonBlocking);
		if (_inotifyFd < 0) {
			throw NativeFailure("inotify_init1");
		}

		_stopFd = eventfd(0, CloseOnExec);
		if (_stopFd < 0) {
			int failed = Marshal.GetLastPInvokeError();
			close(_inotifyFd);
			_inotifyFd = -1;
			throw NativeFailure("eventfd", failed);
		}

		_reader = new Thread(ReadLoop) { IsBackground = true, Name = "weavie-workspace-inotify" };
		_reader.Start();
	}

	private void Add(string path) {
		int watch = inotify_add_watch(_inotifyFd, path, WatchMask);
		if (watch < 0) {
			int error = Marshal.GetLastPInvokeError();
			if (error == NoSuchFileOrDirectory) {
				return;
			}

			throw NativeFailure($"inotify_add_watch('{path}')", error);
		}

		if (_watchPaths.TryGetValue(watch, out string? priorPath)) {
			_pathWatches.Remove(priorPath);
		}

		_pathWatches[path] = watch;
		_watchPaths[watch] = path;
	}

	private void Remove(string path) {
		int watch = _pathWatches[path];
		_pathWatches.Remove(path);
		_watchPaths.Remove(watch);
		_ = inotify_rm_watch(_inotifyFd, watch);
	}

	private unsafe void ReadLoop() {
		try {
			var pollFds = stackalloc PollDescriptor[2];
			pollFds[0].FileDescriptor = _inotifyFd;
			pollFds[0].Events = PollIn;
			pollFds[1].FileDescriptor = _stopFd;
			pollFds[1].Events = PollIn;
			byte[] buffer = new byte[64 * 1024];
			while (true) {
				pollFds[0].ReturnedEvents = 0;
				pollFds[1].ReturnedEvents = 0;
				FlushExpiredMoves();
				int result = poll(pollFds, 2, PendingMoveTimeout());
				if (result < 0) {
					if (Marshal.GetLastPInvokeError() == Interrupted) {
						continue;
					}

					throw NativeFailure("poll");
				}
				if (result == 0) {
					continue;
				}

				if ((pollFds[1].ReturnedEvents & PollIn) != 0) {
					return;
				}
				if ((pollFds[0].ReturnedEvents & PollFailure) != 0
					|| (pollFds[1].ReturnedEvents & PollFailure) != 0) {
					throw new IOException("poll reported a failed workspace watcher descriptor.");
				}

				if ((pollFds[0].ReturnedEvents & PollIn) != 0) {
					ReadAvailableEvents(buffer);
				}
			}
		} catch (Exception ex) {
			lock (_gate) {
				if (_disposed) {
					return;
				}
			}

			_error(ex);
		}
	}

	public void Dispose() {
		Thread? reader;
		int stopFd;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			reader = _reader;
			stopFd = _stopFd;
		}

		if (stopFd >= 0) {
			_ = write(stopFd, BitConverter.GetBytes(1UL), sizeof(ulong));
		}

		reader?.Join();
		lock (_gate) {
			if (_inotifyFd >= 0) {
				close(_inotifyFd);
			}

			if (_stopFd >= 0) {
				close(_stopFd);
			}

			_inotifyFd = -1;
			_stopFd = -1;
			_pathWatches.Clear();
			_watchPaths.Clear();
			_pendingMoves.Clear();
		}
	}

	private static IOException NativeFailure(string operation) =>
		NativeFailure(operation, Marshal.GetLastPInvokeError());

	private static IOException NativeFailure(string operation, int error) =>
		new($"{operation} failed (errno {error}).");

}
