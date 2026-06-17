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

/// <summary>One watched-file change: the file's <c>file://</c> URI and what happened to it.</summary>
/// <param name="Uri">The changed file's <c>file://</c> URI.</param>
/// <param name="Kind">The kind of change.</param>
public readonly record struct WatchedFileChange(string Uri, FileChangeKind Kind);

/// <summary>
/// Watches a workspace tree and reports relevant file changes in debounced batches, so the host can
/// forward <c>workspace/didChangeWatchedFiles</c> to language servers. This is the agentic-editor
/// correctness path (spec §9): Claude edits files on disk — directly and via the IDE-MCP apply flow —
/// and servers must hear about it or their diagnostics/types go stale. Changes are filtered to the
/// languages we serve (by extension) and skip noise directories (<c>node_modules</c>, <c>.git</c>,
/// build output) so a broad workspace root doesn't drown the servers.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable {
	private readonly string _root;
	private readonly IReadOnlySet<string> _extensions;
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
	/// <param name="extensions">File extensions (with leading dot) to report; others are ignored.</param>
	/// <param name="onChanges">Invoked with a debounced batch of changes (off the UI thread).</param>
	/// <param name="log">Diagnostic log sink.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes before flushing a batch.</param>
	public WorkspaceWatcher(
		string root,
		IReadOnlySet<string> extensions,
		Action<IReadOnlyList<WatchedFileChange>> onChanges,
		Action<string> log,
		int debounceMs = 250) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(extensions);
		ArgumentNullException.ThrowIfNull(onChanges);

		_root = root;
		_extensions = extensions;
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
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
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
			_log($"workspace watcher on {_root} ({string.Join(",", _extensions)})");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
			_log($"workspace watcher failed to start: {ex.Message}");
			_watcher?.Dispose();
			_watcher = null;
		}
	}

	private void Record(string fullPath, FileChangeKind kind) {
		if (!IsRelevant(fullPath)) {
			return;
		}

		// Last-write-wins per path within a batch, except a delete after a create cancels to delete and
		// a create after a delete is a change — but coarse last-wins is fine for didChangeWatchedFiles.
		_pending[fullPath] = kind;
		_debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
	}

	private bool IsRelevant(string fullPath) {
		string ext = Path.GetExtension(fullPath);
		if (string.IsNullOrEmpty(ext) || !_extensions.Contains(ext)) {
			return false;
		}

		return !WorkspacePaths.HasIgnoredSegment(fullPath);
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

	/// <inheritdoc/>
	public void Dispose() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		if (_watcher is not null) {
			_watcher.EnableRaisingEvents = false;
			_watcher.Dispose();
		}

		_debounceTimer?.Dispose();
	}
}
