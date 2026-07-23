using System.Collections.Concurrent;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Lsp;

/// <summary>An LSP <c>FileChangeType</c>: a file was created, changed, or deleted.</summary>
public enum FileChangeKind {
	/// <summary>The file was created (LSP <c>FileChangeType.Created</c> = 1).</summary>
	Created = 1,

	/// <summary>The file's contents changed (LSP <c>FileChangeType.Changed</c> = 2).</summary>
	Changed = 2,

	/// <summary>The file was deleted (LSP <c>FileChangeType.Deleted</c> = 3).</summary>
	Deleted = 3,
}

/// <summary>One watched change: the entry's absolute native path and what happened to it.</summary>
/// <param name="Path">The changed file's (or directory's) absolute native path.</param>
/// <param name="Kind">The kind of change.</param>
public readonly record struct WatchedFileChange(string Path, FileChangeKind Kind);

/// <summary>
/// Watches a workspace tree and reports every file and directory change (skipping noise dirs —
/// <c>node_modules</c>, <c>.git</c>, etc.) in debounced batches. Consumers filter to their own scope: the LSP
/// layer to its served extensions for <c>workspace/didChangeWatchedFiles</c> (spec §9), the editor's
/// <c>file://</c> provider to reload on-disk edits, the file browser to re-list changed directories.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable {
	private readonly string _root;
	private readonly Action<IReadOnlyList<WatchedFileChange>> _onChanges;
	private readonly Action<string> _log;
	private readonly TimeSpan _debounce;
	private readonly ConcurrentDictionary<string, FileChangeKind> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly Lock _flushLock = new();

	private FileSystemWatcher? _watcher;
	private Timer? _debounceTimer;
	private bool _disposed;

	/// <summary>Creates a watcher for <paramref name="root"/>. Call <see cref="Start"/> to begin watching.</summary>
	/// <param name="root">The workspace root to watch recursively.</param>
	/// <param name="onChanges">Invoked with a debounced batch of changes (off the UI thread).</param>
	/// <param name="log">Diagnostic log sink.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes before flushing a batch.</param>
	public WorkspaceWatcher(
		string root,
		Action<IReadOnlyList<WatchedFileChange>> onChanges,
		Action<string> log,
		int debounceMs) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(onChanges);

		_root = root;
		_onChanges = onChanges;
		_log = log;
		_debounce = TimeSpan.FromMilliseconds(debounceMs);
	}

	/// <summary>Begins watching. No-op if the root doesn't exist or watching is unavailable.</summary>
	public void Start() {
		if (_watcher is not null || !Directory.Exists(_root)) {
			return;
		}

		try {
			_watcher = new FileSystemWatcher(_root) {
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
					| NotifyFilters.CreationTime | NotifyFilters.Size,
				InternalBufferSize = 64 * 1024,
			};
			_watcher.Created += (_, e) => Record(e.FullPath, FileChangeKind.Created);
			_watcher.Changed += (_, e) => Record(e.FullPath, FileChangeKind.Changed);
			_watcher.Deleted += (_, e) => Record(e.FullPath, FileChangeKind.Deleted);
			_watcher.Renamed += (_, e) => {
				Record(e.OldFullPath, FileChangeKind.Deleted);
				Record(e.FullPath, FileChangeKind.Created);
			};
			_watcher.Error += (_, e) => _log($"workspace watcher error: {e.GetException().Message}");
			_debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
			_watcher.EnableRaisingEvents = true;
			_log($"workspace watcher on {_root}");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
			_log($"workspace watcher failed to start: {ex.Message}");
			_watcher?.Dispose();
			_watcher = null;
		}
	}

	private void Record(string fullPath, FileChangeKind kind) {
		if (WorkspacePaths.HasIgnoredSegment(fullPath)) {
			return;
		}

		// Last-write-wins per path within a batch; coarse last-wins is fine for every consumer's refresh.
		_pending[fullPath] = kind;
		lock (_flushLock) {
			// A watcher callback in flight during Dispose must not touch the disposed timer.
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
					batch.Add(new WatchedFileChange(path, kind));
				}
			}
		}

		if (batch.Count > 0) {
			_onChanges(batch);
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_flushLock) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			_debounceTimer?.Dispose();
		}

		if (_watcher is not null) {
			_watcher.EnableRaisingEvents = false;
			_watcher.Dispose();
		}
	}
}
