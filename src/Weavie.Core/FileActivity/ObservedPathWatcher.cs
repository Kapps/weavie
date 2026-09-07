using System.Collections.Concurrent;
using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

/// <summary>
/// Watches explicitly opened outside files and cached directory listings without walking the workspace.
/// Platform-owned directory watches feed the session's ordinary changed/deleted facts.
/// </summary>
public sealed class ObservedPathWatcher : IDisposable {
	private readonly IFileSystem _fileSystem;
	private readonly IFileActivitySink _sink;
	private readonly Action<string> _onFailure;
	private readonly TimeSpan _debounce;
	private readonly IWorkspaceDirectoryWatchSet _directories;
	private readonly ConcurrentDictionary<string, byte> _pending = new(PathIdentity.Comparer);
	private readonly HashSet<string> _files = new(PathIdentity.Comparer);
	private readonly Dictionary<string, string> _directorySubscriptions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _listedDirectories = new(PathIdentity.Comparer);
	private readonly Lock _gate = new();
	private Timer? _debounceTimer;
	private bool _disposed;

	/// <summary>Reports changes to <paramref name="sink"/>, coalescing bursts over <paramref name="debounceMs"/>.</summary>
	/// <param name="fileSystem">Reads the post-change stat that rides the reported fact.</param>
	/// <param name="sink">The owning session's activity stream.</param>
	/// <param name="onFailure">Surfaces a watch failure to the user; watching is silently over once it fires.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes to one file before reporting it.</param>
	public ObservedPathWatcher(
		IFileSystem fileSystem,
		IFileActivitySink sink,
		Action<string> onFailure,
		int debounceMs)
		: this(fileSystem, sink, onFailure, debounceMs, PlatformWatchSet) { }

	internal ObservedPathWatcher(
		IFileSystem fileSystem,
		IFileActivitySink sink,
		Action<string> onFailure,
		int debounceMs,
		Func<ObservedPathWatcher, IWorkspaceDirectoryWatchSet> createWatchSet) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(sink);
		ArgumentNullException.ThrowIfNull(onFailure);
		ArgumentNullException.ThrowIfNull(createWatchSet);
		_fileSystem = fileSystem;
		_sink = sink;
		_onFailure = onFailure;
		_debounce = TimeSpan.FromMilliseconds(debounceMs);
		_directories = createWatchSet(this);
	}

	// Windows uses minimal recursive roots: persistent descendant handles prevent ancestor renames.
	private static IWorkspaceDirectoryWatchSet PlatformWatchSet(ObservedPathWatcher owner) =>
		OperatingSystem.IsLinux()
			? new LinuxWorkspaceDirectoryWatchSet(
				owner.OnTouched,
				owner.OnTouched,
				owner.OnTouched,
				owner.OnRenamed,
				owner.OnError)
			: new FileSystemWorkspaceDirectoryWatchSet(
				path => new FileSystemWatcher(path),
				owner.OnTouched,
				owner.OnTouched,
				owner.OnTouched,
				owner.OnRenamed,
				owner.OnError,
				recursive: OperatingSystem.IsWindows());

	/// <summary>How many directories are currently watched.</summary>
	public int WatchedDirectoryCount => _directories.Count;

	/// <summary>Observes a directory listing until its owning subscription is released.</summary>
	public void WatchDirectory(string subscriptionId, string directory) {
		ArgumentException.ThrowIfNullOrEmpty(subscriptionId);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			string path = PathIdentity.Normalize(directory);
			bool subscribed = _directorySubscriptions.TryGetValue(subscriptionId, out string? existing);
			if (subscribed && !PathIdentity.Equals(existing!, path)) {
				throw new InvalidOperationException("A directory subscription cannot change its path.");
			}

			_directories.EnsureWatching(path);
			if (subscribed) {
				return;
			}

			_directorySubscriptions.Add(subscriptionId, path);
			_listedDirectories.TryGetValue(path, out int count);
			_listedDirectories[path] = count + 1;
		}
	}

	/// <summary>Releases one listing subscription, retaining watches still owned by other listings or files.</summary>
	public void UnwatchDirectory(string subscriptionId) {
		lock (_gate) {
			if (_disposed || !_directorySubscriptions.Remove(subscriptionId, out string? path)) {
				return;
			}

			int remaining = _listedDirectories[path] - 1;
			if (remaining > 0) {
				_listedDirectories[path] = remaining;
				return;
			}

			_listedDirectories.Remove(path);
			if (!_files.Contains(path)) {
				_pending.TryRemove(path, out _);
			}
			ReconcileWatchesLocked();
		}
	}

	/// <summary>
	/// Observes exactly <paramref name="files"/>, dropping watches and pending reports for the rest. Called
	/// whenever the open tab set changes, so a closed file stops costing a watch.
	/// </summary>
	public void Watch(IReadOnlyList<string> files) {
		ArgumentNullException.ThrowIfNull(files);
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_files.Clear();
			foreach (string file in files) {
				_files.Add(Path.GetFullPath(file));
			}

			foreach (string stale in _pending.Keys.Where(path => !_files.Contains(path) && !_listedDirectories.ContainsKey(path))) {
				_pending.TryRemove(stale, out _);
			}

			ReconcileWatchesLocked();
		}
	}

	// Recomputes the desired watch set (open files' parents + listed directories) and reconciles it. Called
	// under `_gate` whenever either input — open files or listed directories — changes.
	private void ReconcileWatchesLocked() {
		string[] directories = [.. _files
			.Select(Path.GetDirectoryName)
			.OfType<string>()
			.Where(directory => directory.Length > 0)
			.Concat(_listedDirectories.Keys)
			.Distinct(PathIdentity.Comparer)];
		// Reconciling an empty set to an empty set still starts the platform watcher, and a session with no
		// outside files open is the common case — so it would cost every session a native instance for nothing.
		if (directories.Length == 0 && _directories.Count == 0) {
			return;
		}

		try {
			_directories.Reconcile(directories);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Contained: this runs from an event whose other subscribers persist the session.
			_onFailure($"Can't watch opened files and directory listings: {ex.Message}");
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			_files.Clear();
			_directorySubscriptions.Clear();
			_listedDirectories.Clear();
			_pending.Clear();
		}

		_debounceTimer?.Dispose();
		_directories.Dispose();
	}

	// A watched directory reports every file in it, so the filter is what makes this per-file.
	private void OnTouched(FileSystemEventArgs e) {
		Touch(e.FullPath);
		if (e.ChangeType != WatcherChangeTypes.Changed && Path.GetDirectoryName(e.FullPath) is { } parent) {
			Touch(parent);
		}
	}

	private void OnRenamed(string oldPath, string newPath) {
		Touch(oldPath);
		Touch(newPath);
		if (Path.GetDirectoryName(oldPath) is { } oldParent) Touch(oldParent);
		if (Path.GetDirectoryName(newPath) is { } newParent) Touch(newParent);
	}

	private void OnError(Exception error) =>
		_onFailure($"Stopped watching opened files and directory listings: {error.Message}");

	private void Touch(string path) {
		lock (_gate) {
			if (_disposed || (!_files.Contains(path) && !_listedDirectories.ContainsKey(path))) {
				return;
			}

			_pending[path] = 0;
			_debounceTimer ??= new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			_debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
		}
	}

	// Reports under the gate so Dispose, which takes it before disposing the timer, can't leave a callback
	// delivering into an activity stream that is already closing — that throws, and an escaped Timer callback
	// takes the host process with it.
	private void Flush() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			foreach (string path in _pending.Keys.ToArray()) {
				if (!_pending.TryRemove(path, out _)) {
					continue;
				}

				if (_fileSystem.TryGetStat(path, out var revision)) {
					_sink.ReportChanged(path, revision);
				} else {
					_sink.ReportDeleted(path);
				}
			}
		}
	}
}
