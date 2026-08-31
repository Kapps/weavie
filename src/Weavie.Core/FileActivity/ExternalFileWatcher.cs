using System.Collections.Concurrent;
using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

/// <summary>
/// Watches the individual files the editor has open from outside its checkout. The workspace watcher is
/// recursive over the worktree and deliberately stays that way, so nothing observed those files: an edit made
/// elsewhere never reached the open buffer, and autosave could write over it. This holds one watch per
/// containing directory, filtered to the exact files, so the cost tracks open tabs rather than whatever sits
/// above them. Changes enter the session's stream as the ordinary changed/deleted facts.
/// </summary>
public sealed class ExternalFileWatcher : IDisposable {
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private readonly IFileSystem _fileSystem;
	private readonly IFileActivitySink _sink;
	private readonly Action<string> _log;
	private readonly TimeSpan _debounce;
	private readonly IWorkspaceDirectoryWatchSet _directories;
	private readonly ConcurrentDictionary<string, byte> _pending = new(PathComparer);
	private readonly HashSet<string> _files = new(PathComparer);
	private readonly Lock _gate = new();
	private Timer? _debounceTimer;
	private bool _disposed;

	/// <summary>Reports changes to <paramref name="sink"/>, coalescing bursts over <paramref name="debounceMs"/>.</summary>
	/// <param name="fileSystem">Reads the post-change stat that rides the reported fact.</param>
	/// <param name="sink">The owning session's activity stream.</param>
	/// <param name="log">Diagnostic log sink.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes to one file before reporting it.</param>
	public ExternalFileWatcher(
		IFileSystem fileSystem,
		IFileActivitySink sink,
		Action<string> log,
		int debounceMs)
		: this(fileSystem, sink, log, debounceMs, usePlatformWatcher: true) { }

	internal ExternalFileWatcher(
		IFileSystem fileSystem,
		IFileActivitySink sink,
		Action<string> log,
		int debounceMs,
		bool usePlatformWatcher) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(sink);
		ArgumentNullException.ThrowIfNull(log);
		_fileSystem = fileSystem;
		_sink = sink;
		_log = log;
		_debounce = TimeSpan.FromMilliseconds(debounceMs);
		// The same flat watch sets the workspace watcher uses; only the recursive one is unusable here, because
		// these files sit in unrelated directories rather than under one root.
		_directories = usePlatformWatcher && OperatingSystem.IsLinux()
			? new LinuxWorkspaceDirectoryWatchSet(OnTouched, OnTouched, OnTouched, OnRenamed, OnError)
			: new FileSystemWorkspaceDirectoryWatchSet(
				path => new FileSystemWatcher(path),
				OnTouched,
				OnTouched,
				OnTouched,
				OnRenamed,
				OnError);
	}

	/// <summary>How many directories are currently watched.</summary>
	public int WatchedDirectoryCount => _directories.Count;

	/// <summary>
	/// Observes exactly <paramref name="files"/>, dropping watches for files no longer among them. Called
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

			_directories.Reconcile([.. _files
				.Select(Path.GetDirectoryName)
				.OfType<string>()
				.Where(directory => directory.Length > 0)
				.Distinct(PathComparer)]);
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
		}

		_debounceTimer?.Dispose();
		_directories.Dispose();
	}

	// A watched directory reports every file in it, so the filter is what makes this per-file.
	private void OnTouched(FileSystemEventArgs e) => Touch(e.FullPath);

	private void OnRenamed(string oldPath, string newPath) {
		Touch(oldPath);
		Touch(newPath);
	}

	private void OnError(Exception error) => _log($"external file watch failed: {error.Message}");

	private void Touch(string path) {
		lock (_gate) {
			if (_disposed || !_files.Contains(path)) {
				return;
			}

			_pending[path] = 0;
			_debounceTimer ??= new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			_debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
		}
	}

	private void Flush() {
		foreach (string path in _pending.Keys.ToArray()) {
			if (!_pending.TryRemove(path, out _)) {
				continue;
			}

			try {
				if (_fileSystem.FileExists(path) && _fileSystem.TryGetStat(path, out var revision)) {
					_sink.ReportChanged(path, revision);
				} else {
					_sink.ReportDeleted(path);
				}
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				_log($"external file watch could not read {path}: {ex.Message}");
			}
		}
	}
}
