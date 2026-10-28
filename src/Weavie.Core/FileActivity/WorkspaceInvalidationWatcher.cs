using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Core.FileActivity;

/// <summary>
/// Reports generic workspace invalidations from an authoritative file inventory. Platform watch sets observe
/// only inventoried paths; filtering for domain-specific consumers belongs in their projections.
/// </summary>
public sealed partial class WorkspaceInvalidationWatcher : IDisposable {
	private readonly WorkspaceInventory _inventory;
	private readonly Action<IReadOnlyList<FileInvalidation>> _onChanges;
	private readonly Action<string> _log;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeSpan _debounce;
	private readonly ConcurrentDictionary<string, FileInvalidationKind> _pending = new(PathIdentity.Comparer);
	private readonly IWorkspaceDirectoryWatchSet _directoryWatchers;
	private readonly Channel<bool> _refreshSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
		FullMode = BoundedChannelFullMode.DropWrite,
		SingleReader = true,
		SingleWriter = false,
	});
	private readonly Lock _flushLock = new();
	private readonly Lock _dirtyLock = new();
	private HashSet<string> _dirtyPaths = new(PathIdentity.Comparer);
	private bool _dirtyAll;
	private readonly CancellationTokenSource _stopping = new();
	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private IReadOnlySet<string> _files;
	private bool _isRepository;
	private Exception? _watcherFailure;
	private Timer? _debounceTimer;
	private Task? _stopTask;
	private bool _runStarted;
	private bool _disposed;

	/// <summary>Creates an inventory-driven watcher. Call <see cref="RunAsync"/> to begin watching.</summary>
	/// <param name="inventory">The authoritative workspace file and directory inventory.</param>
	/// <param name="onChanges">Invoked with a debounced native-path batch off the UI thread.</param>
	/// <param name="log">Diagnostic log sink.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes before flushing a batch.</param>
	public WorkspaceInvalidationWatcher(
		WorkspaceInventory inventory,
		Action<IReadOnlyList<FileInvalidation>> onChanges,
		Action<string> log,
		int debounceMs)
		: this(
			inventory,
			onChanges,
			log,
			debounceMs,
			Task.Delay,
			path => new FileSystemWatcher(path),
			usePlatformWatcher: true) { }

	internal WorkspaceInvalidationWatcher(
		WorkspaceInventory inventory,
		Action<IReadOnlyList<FileInvalidation>> onChanges,
		Action<string> log,
		int debounceMs,
		Func<TimeSpan, CancellationToken, Task> delay,
		Func<string, FileSystemWatcher> createWatcher)
		: this(
			inventory,
			onChanges,
			log,
			debounceMs,
			delay,
			createWatcher,
			usePlatformWatcher: false) { }

	private WorkspaceInvalidationWatcher(
		WorkspaceInventory inventory,
		Action<IReadOnlyList<FileInvalidation>> onChanges,
		Action<string> log,
		int debounceMs,
		Func<TimeSpan, CancellationToken, Task> delay,
		Func<string, FileSystemWatcher> createWatcher,
		bool usePlatformWatcher) {
		ArgumentNullException.ThrowIfNull(inventory);
		ArgumentNullException.ThrowIfNull(onChanges);
		ArgumentNullException.ThrowIfNull(log);
		ArgumentNullException.ThrowIfNull(delay);
		ArgumentNullException.ThrowIfNull(createWatcher);
		if (debounceMs < 0) {
			throw new ArgumentOutOfRangeException(nameof(debounceMs));
		}

		_inventory = inventory;
		_onChanges = onChanges;
		_log = log;
		_delay = delay;
		_debounce = TimeSpan.FromMilliseconds(debounceMs);
		_directoryWatchers = usePlatformWatcher && OperatingSystem.IsLinux()
			? new LinuxWorkspaceDirectoryWatchSet(OnCreated, OnChanged, OnDeleted, OnRenamed, OnError)
			: usePlatformWatcher
			? new RecursiveWorkspaceDirectoryWatchSet(
				inventory.Root,
				OnCreated,
				OnChanged,
				OnDeleted,
				OnRenamed,
				OnError)
			: new FileSystemWorkspaceDirectoryWatchSet(
				createWatcher,
				OnCreated,
				OnChanged,
				OnDeleted,
				OnRenamed,
				OnError,
				recursive: false);
		_files = new HashSet<string>(PathIdentity.Comparer);
	}

	/// <summary>Completes when the initial inventory is loaded and platform watches are installed.</summary>
	public Task Ready => _ready.Task;

	/// <summary>
	/// The inventory this watcher keeps current, once its initial load completes; a fresh listing when
	/// observation failed or ended, since nothing then keeps the snapshot current.
	/// </summary>
	public async Task<WorkspaceInventorySnapshot> SnapshotAsync(CancellationToken ct) {
		try {
			await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
		} catch (Exception) when (!ct.IsCancellationRequested) {
			// The observation failure reaches the user through the run loop; the listing below reports its own.
		}

		return !_finished.Task.IsCompleted && _inventory.LastSnapshot is { } snapshot
			? snapshot
			: await _inventory.RefreshAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Loads the inventory asynchronously, then watches its directories until cancelled or stopped. Only an
	/// event that can change which paths the workspace contains re-enumerates it. Throws when the authoritative
	/// inventory or platform watcher fails.
	/// </summary>
	public async Task RunAsync(CancellationToken ct) {
		lock (_flushLock) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_runStarted) {
				throw new InvalidOperationException("Workspace observation is already running.");
			}
			_runStarted = true;
		}

		using var runStopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
		var runCt = runStopping.Token;
		try {
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
			lock (_flushLock) {
				if (_disposed) {
					_ready.TrySetException(new ObjectDisposedException(nameof(WorkspaceInvalidationWatcher)));
					return;
				}
				_debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
			}

			_inventory.Changed += SignalFullRefresh;
			try {
				var cooldown = Task.CompletedTask;
				await RefreshAsync(initial: true, scope: null, runCt).ConfigureAwait(false);
				_ready.TrySetResult();
				while (await _refreshSignals.Reader.WaitToReadAsync(runCt).ConfigureAwait(false)) {
					// A watcher failure is fatal and reported before the cooldown, which only paces re-enumeration.
					if (Interlocked.Exchange(ref _watcherFailure, null) is { } failure) {
						throw new IOException("Workspace file watching failed.", failure);
					}

					await cooldown.ConfigureAwait(false);
					while (_refreshSignals.Reader.TryRead(out _)) { }
					long started = Stopwatch.GetTimestamp();
					await RefreshAsync(initial: false, TakeDirtyScope(), runCt).ConfigureAwait(false);
					cooldown = _delay(CooldownAfter(Stopwatch.GetElapsedTime(started)), runCt);
				}
			} finally {
				_inventory.Changed -= SignalFullRefresh;
			}
		} catch (OperationCanceledException) when (_stopping.IsCancellationRequested) {
			_ready.TrySetCanceled(_stopping.Token);
		} catch (Exception ex) {
			_ready.TrySetException(ex);
			throw;
		} finally {
			_directoryWatchers.Dispose();
			_finished.TrySetResult();
		}
	}

	/// <summary>Stops observation, flushes pending invalidations, and waits for the run loop to finish.</summary>
	public Task StopAsync() {
		lock (_flushLock) {
			return _stopTask ??= StopCoreAsync();
		}
	}

	private async Task StopCoreAsync() {
		await Task.Yield();
		Timer? timer;
		bool waitForRun;
		lock (_flushLock) {
			_disposed = true;
			timer = _debounceTimer;
			_debounceTimer = null;
			waitForRun = _runStarted;
		}

		timer?.Dispose();
		_ready.TrySetCanceled();
		_stopping.Cancel();
		_directoryWatchers.Dispose();
		_refreshSignals.Writer.TryComplete();
		FlushPendingOnStop();
		if (waitForRun) {
			await _finished.Task.ConfigureAwait(false);
		}
		_stopping.Dispose();
	}

	// Spacing re-enumerations by the previous pass keeps a burst (a checkout, an install) under half this loop.
	private TimeSpan CooldownAfter(TimeSpan lastRefresh) => lastRefresh > _debounce ? lastRefresh : _debounce;

	// A null scope re-lists the whole workspace; otherwise only the changed paths are re-listed.
	private async Task RefreshAsync(bool initial, IReadOnlyCollection<string>? scope, CancellationToken ct) {
		scope = scope is null ? null : _inventory.CoveringScope(scope);
		bool watchesChanged;
		do {
			var previous = _inventory.LastSnapshot;
			var snapshot = scope is null
				? await _inventory.RefreshAsync(ct).ConfigureAwait(false)
				: await _inventory.RefreshAsync(scope, ct).ConfigureAwait(false);
			if (!initial && ReferenceEquals(snapshot, previous)) {
				return;
			}

			Volatile.Write(ref _isRepository, snapshot.IsRepository);
			var nextFiles = snapshot.Files.ToHashSet(PathIdentity.Comparer);
			if (!initial) {
				foreach (string path in nextFiles.Except(_files, PathIdentity.Comparer)) {
					Record(path, FileInvalidationKind.Created);
				}

				foreach (string path in _files.Except(nextFiles, PathIdentity.Comparer)) {
					Record(path, FileInvalidationKind.Deleted);
				}
			}

			Volatile.Write(ref _files, nextFiles);
			watchesChanged = _directoryWatchers.Reconcile(snapshot.Directories);
			if (watchesChanged) {
				_log($"workspace watcher on {_inventory.Root} ({_directoryWatchers.Count} flat directories)");
			}
			initial = false;
			// Snapshot after installing new watches so changes during discovery cannot fall into an unwatched gap.
		} while (watchesChanged);
	}

	private void SignalRefresh(string path) {
		lock (_dirtyLock) {
			_dirtyPaths.Add(path);
		}
		_refreshSignals.Writer.TryWrite(true);
	}

	private void SignalFullRefresh() {
		lock (_dirtyLock) {
			_dirtyAll = true;
		}
		_refreshSignals.Writer.TryWrite(true);
	}

	private IReadOnlyCollection<string>? TakeDirtyScope() {
		lock (_dirtyLock) {
			var scope = _dirtyAll || !Volatile.Read(ref _isRepository) ? null : _dirtyPaths;
			_dirtyPaths = new HashSet<string>(PathIdentity.Comparer);
			_dirtyAll = false;
			return scope;
		}
	}

	/// <inheritdoc/>
	public void Dispose() => StopAsync().GetAwaiter().GetResult();

	private void FlushPendingOnStop() {
		lock (_flushLock) {
			DeliverPendingLocked();
		}
	}

	private void DeliverPendingLocked() {
		if (_pending.IsEmpty) {
			return;
		}

		var batch = new List<FileInvalidation>(_pending.Count);
		foreach (var (path, kind) in _pending) {
			if (_pending.TryRemove(path, out _)) {
				batch.Add(new FileInvalidation(path, kind));
			}
		}

		if (batch.Count > 0) {
			_onChanges(batch);
		}
	}
}
