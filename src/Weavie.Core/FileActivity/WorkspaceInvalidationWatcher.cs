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
				OnError);
		_files = new HashSet<string>(PathIdentity.Comparer);
	}

	/// <summary>Completes when the initial inventory is loaded and platform watches are installed.</summary>
	public Task Ready => _ready.Task;

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

			_inventory.Changed += SignalRefresh;
			try {
				var cooldown = Task.CompletedTask;
				await RefreshAsync(initial: true, runCt).ConfigureAwait(false);
				_ready.TrySetResult();
				while (await _refreshSignals.Reader.WaitToReadAsync(runCt).ConfigureAwait(false)) {
					// A watcher failure is fatal and reported before the cooldown, which only paces re-enumeration.
					if (Interlocked.Exchange(ref _watcherFailure, null) is { } failure) {
						throw new IOException("Workspace file watching failed.", failure);
					}

					await cooldown.ConfigureAwait(false);
					while (_refreshSignals.Reader.TryRead(out _)) { }
					long started = Stopwatch.GetTimestamp();
					await RefreshAsync(initial: false, runCt).ConfigureAwait(false);
					cooldown = _delay(CooldownAfter(Stopwatch.GetElapsedTime(started)), runCt);
				}
			} finally {
				_inventory.Changed -= SignalRefresh;
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

	private async Task RefreshAsync(bool initial, CancellationToken ct) {
		var snapshot = await _inventory.RefreshAsync(ct).ConfigureAwait(false);
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
		if (_directoryWatchers.Reconcile(snapshot.Directories)) {
			_log($"workspace watcher on {_inventory.Root} ({_directoryWatchers.Count} flat directories)");
		}
	}

	private void SignalRefresh() => _refreshSignals.Writer.TryWrite(true);

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
