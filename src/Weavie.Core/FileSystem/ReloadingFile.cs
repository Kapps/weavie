namespace Weavie.Core.FileSystem;

internal readonly record struct FileReload<T>(
	T PreviousValue,
	T Value,
	Exception? PreviousError,
	Exception? Error);

internal sealed class ReloadingFile<T> : IDisposable {
	private const int DebounceMilliseconds = 250;

	private readonly Lock _gate;
	private readonly Func<string, T> _load;
	private FileSystemWatcher? _watcher;
	private Timer? _debounce;
	private T _value;
	private Exception? _error;
	private bool _disposed;

	internal ReloadingFile(
		string path,
		Lock gate,
		T initialValue,
		Func<string, T> load,
		bool watch) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(load);
		Path = path;
		_gate = gate;
		_value = initialValue;
		_load = load;
		if (watch) {
			Watch();
		}
		lock (_gate) {
			ReloadLocked();
		}
	}

	internal event Action<FileReload<T>>? Reloaded;

	internal string Path { get; }

	internal T Value {
		get { lock (_gate) { return _value; } }
	}

	internal Exception? Error {
		get { lock (_gate) { return _error; } }
	}

	internal FileReload<T> Reload() {
		FileReload<T> reload;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			reload = ReloadLocked();
		}
		Reloaded?.Invoke(reload);
		return reload;
	}

	internal void Watch() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_watcher is not null) {
				return;
			}

			string? directory = System.IO.Path.GetDirectoryName(Path);
			if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) {
				return;
			}

			var debounce = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
			FileSystemWatcher? watcher = null;
			try {
				watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(Path)) {
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
				};
				watcher.Changed += OnFileEvent;
				watcher.Created += OnFileEvent;
				watcher.Deleted += OnFileEvent;
				watcher.Renamed += OnFileEvent;
				watcher.Error += OnWatcherError;
				watcher.EnableRaisingEvents = true;
				_debounce = debounce;
				_watcher = watcher;
			} catch {
				watcher?.Dispose();
				debounce.Dispose();
				throw;
			}
		}
	}

	public void Dispose() {
		FileSystemWatcher? watcher;
		Timer? debounce;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			watcher = _watcher;
			debounce = _debounce;
			_watcher = null;
			_debounce = null;
		}

		if (watcher is not null) {
			watcher.EnableRaisingEvents = false;
			watcher.Changed -= OnFileEvent;
			watcher.Created -= OnFileEvent;
			watcher.Deleted -= OnFileEvent;
			watcher.Renamed -= OnFileEvent;
			watcher.Error -= OnWatcherError;
			watcher.Dispose();
		}
		debounce?.Dispose();
	}

	private FileReload<T> ReloadLocked() {
		var previousValue = _value;
		var previousError = _error;
		try {
			_value = _load(Path);
			_error = null;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) {
			_error = ex;
		}
		return new FileReload<T>(previousValue, _value, previousError, _error);
	}

	private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleReload();

	private void OnWatcherError(object sender, ErrorEventArgs e) => ScheduleReload();

	private void ScheduleReload() {
		lock (_gate) {
			if (!_disposed) {
				_debounce?.Change(DebounceMilliseconds, Timeout.Infinite);
			}
		}
	}

	private void OnDebounceElapsed(object? state) {
		FileReload<T> reload;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			reload = ReloadLocked();
		}
		Reloaded?.Invoke(reload);
	}
}
