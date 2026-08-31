using System.Collections.Concurrent;
using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

/// <summary>
/// Watches the individual files the editor has open from outside its checkout, which the worktree-recursive
/// workspace watcher never sees. One watch per containing directory, filtered to the exact files, so the cost
/// tracks open tabs. Changes enter the session's stream as the ordinary changed/deleted facts.
/// </summary>
public sealed class ExternalFileWatcher : IDisposable {
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private readonly IFileSystem _fileSystem;
	private readonly IFileActivitySink _sink;
	private readonly Action<string> _onFailure;
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
	/// <param name="onFailure">Surfaces a watch failure to the user; watching is silently over once it fires.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes to one file before reporting it.</param>
	public ExternalFileWatcher(
		IFileSystem fileSystem,
		IFileActivitySink sink,
		Action<string> onFailure,
		int debounceMs)
		: this(fileSystem, sink, onFailure, debounceMs, PlatformWatchSet) { }

	internal ExternalFileWatcher(
		IFileSystem fileSystem,
		IFileActivitySink sink,
		Action<string> onFailure,
		int debounceMs,
		Func<ExternalFileWatcher, IWorkspaceDirectoryWatchSet> createWatchSet) {
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

	// The flat watch sets the workspace watcher also picks between; only its recursive one is unusable here,
	// because these files sit in unrelated directories rather than under one root.
	private static IWorkspaceDirectoryWatchSet PlatformWatchSet(ExternalFileWatcher owner) =>
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
				owner.OnError);

	/// <summary>How many directories are currently watched.</summary>
	public int WatchedDirectoryCount => _directories.Count;

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

			foreach (string stale in _pending.Keys.Where(path => !_files.Contains(path))) {
				_pending.TryRemove(stale, out _);
			}

			string[] directories = [.. _files
				.Select(Path.GetDirectoryName)
				.OfType<string>()
				.Where(directory => directory.Length > 0)
				.Distinct(PathComparer)];
			// Reconciling an empty set to an empty set still starts the platform watcher, and a session with no
			// outside files open is the common case — so it would cost every session a native instance for nothing.
			if (directories.Length == 0 && _directories.Count == 0) {
				return;
			}

			try {
				_directories.Reconcile(directories);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Contained: Watch runs from an event whose other subscribers persist the session.
				_onFailure($"Can't watch files opened from outside this workspace: {ex.Message}");
			}
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
			_pending.Clear();
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

	private void OnError(Exception error) =>
		_onFailure($"Stopped watching files opened from outside this workspace: {error.Message}");

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

				if (_fileSystem.FileExists(path) && _fileSystem.TryGetStat(path, out var revision)) {
					_sink.ReportChanged(path, revision);
				} else {
					_sink.ReportDeleted(path);
				}
			}
		}
	}
}
