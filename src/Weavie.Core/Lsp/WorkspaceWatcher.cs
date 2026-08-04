using System.Collections.Concurrent;
using System.Threading.Channels;
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
/// Reports relevant workspace file changes in debounced batches. Git supplies the authoritative file and
/// directory inventory; platform watch sets observe those paths without walking ignored Linux trees.
/// </summary>
public sealed partial class WorkspaceWatcher : IDisposable {
	private static readonly TimeSpan InventoryRefreshInterval = TimeSpan.FromSeconds(2);
	private readonly WorkspaceInventory _inventory;
	private readonly IReadOnlySet<string> _extensions;
	private readonly Action<IReadOnlyList<WatchedFileChange>> _onChanges;
	private readonly Action<string> _log;
	private readonly TimeSpan _debounce;
	private readonly TimeSpan _refreshInterval;
	private readonly ConcurrentDictionary<string, FileChangeKind> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly IWorkspaceDirectoryWatchSet _directoryWatchers;
	private readonly Channel<bool> _refreshSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
		FullMode = BoundedChannelFullMode.DropWrite,
		SingleReader = true,
		SingleWriter = false,
	});
	private readonly Lock _flushLock = new();
	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private IReadOnlySet<string> _files;
	private bool _isRepository;
	private Exception? _watcherFailure;
	private Timer? _debounceTimer;
	private bool _disposed;

	/// <summary>Creates an inventory-driven watcher. Call <see cref="RunAsync"/> to begin watching.</summary>
	/// <param name="inventory">The authoritative Git file and directory inventory.</param>
	/// <param name="extensions">File extensions (with leading dot) to report; others are ignored.</param>
	/// <param name="onChanges">Invoked with a debounced batch of changes (off the UI thread).</param>
	/// <param name="log">Diagnostic log sink.</param>
	/// <param name="debounceMs">How long to coalesce rapid changes before flushing a batch.</param>
	public WorkspaceWatcher(
		WorkspaceInventory inventory,
		IReadOnlySet<string> extensions,
		Action<IReadOnlyList<WatchedFileChange>> onChanges,
		Action<string> log,
		int debounceMs)
		: this(
			inventory,
			extensions,
			onChanges,
			log,
			debounceMs,
			InventoryRefreshInterval,
			path => new FileSystemWatcher(path),
			usePlatformWatcher: true) { }

	internal WorkspaceWatcher(
		WorkspaceInventory inventory,
		IReadOnlySet<string> extensions,
		Action<IReadOnlyList<WatchedFileChange>> onChanges,
		Action<string> log,
		int debounceMs,
		TimeSpan refreshInterval,
		Func<string, FileSystemWatcher> createWatcher)
		: this(
			inventory,
			extensions,
			onChanges,
			log,
			debounceMs,
			refreshInterval,
			createWatcher,
			usePlatformWatcher: false) { }

	private WorkspaceWatcher(
		WorkspaceInventory inventory,
		IReadOnlySet<string> extensions,
		Action<IReadOnlyList<WatchedFileChange>> onChanges,
		Action<string> log,
		int debounceMs,
		TimeSpan refreshInterval,
		Func<string, FileSystemWatcher> createWatcher,
		bool usePlatformWatcher) {
		ArgumentNullException.ThrowIfNull(inventory);
		ArgumentNullException.ThrowIfNull(extensions);
		ArgumentNullException.ThrowIfNull(onChanges);
		ArgumentNullException.ThrowIfNull(log);
		ArgumentNullException.ThrowIfNull(createWatcher);
		if (debounceMs < 0) {
			throw new ArgumentOutOfRangeException(nameof(debounceMs));
		}

		if (refreshInterval <= TimeSpan.Zero) {
			throw new ArgumentOutOfRangeException(nameof(refreshInterval));
		}

		_inventory = inventory;
		_extensions = extensions;
		_onChanges = onChanges;
		_log = log;
		_debounce = TimeSpan.FromMilliseconds(debounceMs);
		_refreshInterval = refreshInterval;
		var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
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
		_files = new HashSet<string>(comparer);
	}

	internal Task Ready => _ready.Task;

	/// <summary>
	/// Loads the inventory asynchronously, then watches its directories until cancelled. Throws when the
	/// authoritative inventory cannot be loaded.
	/// </summary>
	public async Task RunAsync(CancellationToken ct) {
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		if (_disposed) {
			return;
		}

		_debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
		_inventory.Changed += SignalRefresh;
		try {
			await RefreshAsync(initial: true, ct).ConfigureAwait(false);
			_ready.TrySetResult();
			using var timer = new PeriodicTimer(_refreshInterval);
			var tick = timer.WaitForNextTickAsync(ct).AsTask();
			var signal = _refreshSignals.Reader.WaitToReadAsync(ct).AsTask();
			while (true) {
				Task completed = await Task.WhenAny(signal, tick).ConfigureAwait(false);
				if (completed == signal) {
					if (!await signal.ConfigureAwait(false)) {
						break;
					}

					while (_refreshSignals.Reader.TryRead(out _)) { }
					signal = _refreshSignals.Reader.WaitToReadAsync(ct).AsTask();
				}

				if (tick.IsCompleted) {
					if (!await tick.ConfigureAwait(false)) {
						break;
					}

					tick = timer.WaitForNextTickAsync(ct).AsTask();
				}

				if (Interlocked.Exchange(ref _watcherFailure, null) is { } failure) {
					throw new IOException("Workspace file watching failed.", failure);
				}

				await RefreshAsync(initial: false, ct).ConfigureAwait(false);
			}
		} catch (Exception ex) {
			_ready.TrySetException(ex);
			throw;
		} finally {
			_inventory.Changed -= SignalRefresh;
			_directoryWatchers.Dispose();
		}
	}

	private async Task RefreshAsync(bool initial, CancellationToken ct) {
		var snapshot = await _inventory.RefreshAsync(ct).ConfigureAwait(false);
		Volatile.Write(ref _isRepository, snapshot.IsRepository);
		var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var nextFiles = snapshot.Files.ToHashSet(comparer);
		if (!initial) {
			foreach (string path in nextFiles.Except(_files, comparer)) {
				Record(path, FileChangeKind.Created);
			}

			foreach (string path in _files.Except(nextFiles, comparer)) {
				Record(path, FileChangeKind.Deleted);
			}
		}

		Volatile.Write(ref _files, nextFiles);
		ReconcileWatchers(snapshot.Directories);
	}

	private void ReconcileWatchers(IReadOnlyList<string> directories) {
		_directoryWatchers.Reconcile(directories);
		_log($"workspace watcher on {_inventory.Root} ({_directoryWatchers.Count} flat directories; {string.Join(",", _extensions)})");
	}

	private void SignalRefresh() => _refreshSignals.Writer.TryWrite(true);

	/// <inheritdoc/>
	public void Dispose() {
		lock (_flushLock) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			_debounceTimer?.Dispose();
		}

		_refreshSignals.Writer.TryComplete();
		_directoryWatchers.Dispose();
	}
}
