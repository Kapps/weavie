using Weavie.Core.Workspaces;

namespace Weavie.Core.FileActivity;

/// <summary>
/// Watches a workspace tree and reports native-path invalidations in debounced batches. Stop flushes pending
/// invalidations and waits for in-flight delivery, so its owner can drain without losing admitted work.
/// </summary>
public sealed class WorkspaceInvalidationWatcher : IDisposable {
	private readonly string _root;
	private readonly Action<IReadOnlyList<FileInvalidation>> _onChanges;
	private readonly Action<string> _log;
	private readonly TimeSpan _debounce;
	private readonly Dictionary<string, FileInvalidationKind> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly Lock _gate = new();
	private TaskCompletionSource _idle = CompletedSource();

	private FileSystemWatcher? _watcher;
	private Timer? _debounceTimer;
	private int _deliveries;
	private bool _stopping;

	/// <summary>Creates a dormant watcher for <paramref name="root"/>.</summary>
	public WorkspaceInvalidationWatcher(
		string root,
		Action<IReadOnlyList<FileInvalidation>> onChanges,
		Action<string> log,
		int debounceMs) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(onChanges);
		ArgumentNullException.ThrowIfNull(log);
		_root = Path.GetFullPath(root);
		_onChanges = onChanges;
		_log = log;
		_debounce = TimeSpan.FromMilliseconds(debounceMs);
	}

	/// <summary>Begins watching. No-op if the root does not exist or watching is unavailable.</summary>
	public void Start() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_stopping, this);
			if (_watcher is not null || !Directory.Exists(_root)) {
				return;
			}

			try {
				_watcher = new FileSystemWatcher(_root) {
					IncludeSubdirectories = true,
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
					InternalBufferSize = 64 * 1024,
				};
				_watcher.Created += (_, e) => Record(e.FullPath, FileInvalidationKind.Created);
				_watcher.Changed += (_, e) => Record(e.FullPath, FileInvalidationKind.Changed);
				_watcher.Deleted += (_, e) => Record(e.FullPath, FileInvalidationKind.Deleted);
				_watcher.Renamed += (_, e) => {
					Record(e.OldFullPath, FileInvalidationKind.Deleted);
					Record(e.FullPath, FileInvalidationKind.Created);
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
	}

	/// <summary>Stops new observation, flushes pending invalidations, and waits for active delivery.</summary>
	public async Task StopAsync() {
		IReadOnlyList<FileInvalidation> pending;
		Task idle;
		FileSystemWatcher? watcher = null;
		Timer? timer = null;
		lock (_gate) {
			if (_stopping) {
				idle = _idle.Task;
				pending = [];
			} else {
				_stopping = true;
				watcher = _watcher;
				_watcher = null;
				timer = _debounceTimer;
				_debounceTimer = null;
				pending = TakePendingLocked();
				if (pending.Count > 0) {
					BeginDeliveryLocked();
				}
				idle = _idle.Task;
			}
		}

		if (watcher is not null) {
			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
		}
		timer?.Dispose();
		if (pending.Count > 0) {
			Deliver(pending);
		}
		await idle.ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public void Dispose() => StopAsync().GetAwaiter().GetResult();

	internal void Record(string fullPath, FileInvalidationKind kind) {
		if (WorkspacePaths.HasIgnoredSegment(fullPath)) {
			return;
		}

		lock (_gate) {
			if (_stopping) {
				return;
			}
			_pending[Path.GetFullPath(fullPath)] = kind;
			_debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
		}
	}

	private void Flush() {
		IReadOnlyList<FileInvalidation> batch;
		lock (_gate) {
			if (_stopping) {
				return;
			}
			batch = TakePendingLocked();
			if (batch.Count == 0) {
				return;
			}
			BeginDeliveryLocked();
		}
		Deliver(batch);
	}

	private IReadOnlyList<FileInvalidation> TakePendingLocked() {
		if (_pending.Count == 0) {
			return [];
		}
		var batch = _pending.Select(pair => new FileInvalidation(pair.Key, pair.Value)).ToArray();
		_pending.Clear();
		return batch;
	}

	private void BeginDeliveryLocked() {
		if (_deliveries++ == 0) {
			_idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}
	}

	private void Deliver(IReadOnlyList<FileInvalidation> batch) {
		try {
			_onChanges(batch);
		} finally {
			lock (_gate) {
				if (--_deliveries == 0) {
					_idle.TrySetResult();
				}
			}
		}
	}

	private static TaskCompletionSource CompletedSource() {
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		completed.SetResult();
		return completed;
	}
}
