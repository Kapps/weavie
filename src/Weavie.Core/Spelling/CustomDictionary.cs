using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Weavie.Core.Spelling;

/// <summary>A normalized, file-backed custom dictionary shared by the Project or User spelling scope.</summary>
public sealed class CustomDictionary : IDisposable {
	private static readonly FrozenSet<string> EmptyWords = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private readonly Lock _gate = new();
	private readonly DictionaryStorage _storage;
	private readonly Mutex _writeMutex;
	private readonly bool _watch;
	private readonly Timer? _debounce;

	private FrozenSet<string> _words = EmptyWords;
	private SpellDictionaryException? _lastLoadError;
	private FileSystemWatcher? _watcher;
	private bool _disposed;

	/// <summary>Creates a user-scoped dictionary. User paths may use symbolic links.</summary>
	public CustomDictionary(string filePath, bool enableWatcher)
		: this(DictionaryStorage.User(filePath), enableWatcher) {
	}

	/// <summary>Creates a project-scoped dictionary confined to a non-link workspace root.</summary>
	public static CustomDictionary ForProject(string workspaceRoot, bool enableWatcher) =>
		new(DictionaryStorage.Project(workspaceRoot), enableWatcher);

	private CustomDictionary(DictionaryStorage storage, bool enableWatcher) {
		_storage = storage;
		FilePath = storage.FilePath;
		_writeMutex = new Mutex(false, MutexName(FilePath));
		_watch = enableWatcher;
		_debounce = enableWatcher ? new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite) : null;
		try {
			LoadInitialSnapshot();
			SetWatcher();
		} catch {
			_debounce?.Dispose();
			_writeMutex.Dispose();
			throw;
		}
	}

	/// <summary>Raised after the in-memory word snapshot changes.</summary>
	public event Action? Changed;

	/// <summary>Raised after a watched external edit cannot be loaded; the previous good snapshot remains active.</summary>
	public event Action<Exception>? LoadFailed;

	/// <summary>Raised when the current load error appears or is repaired. <c>null</c> means the file is valid again.</summary>
	public event Action<SpellDictionaryException?>? LoadErrorChanged;

	/// <summary>The dictionary's backing file.</summary>
	public string FilePath { get; }

	/// <summary>A thread-safe snapshot of the normalized custom words.</summary>
	public IReadOnlySet<string> Words => Volatile.Read(ref _words);

	/// <summary>The current unreadable-file error, if any. The last good (or empty) snapshot remains active.</summary>
	public SpellDictionaryException? LastLoadError {
		get {
			lock (_gate) {
				return _lastLoadError;
			}
		}
	}

	/// <summary>Returns whether <paramref name="word"/> is present after NFC and apostrophe normalization.</summary>
	public bool Contains(string word) => SpellWord.TryNormalize(word, out string normalized) && Words.Contains(normalized);

	/// <summary>Adds <paramref name="word"/> if absent, creating the file and parent directory only for this write.</summary>
	public void Add(string word) {
		string normalized = SpellWord.RequireNormalized(word, nameof(word));
		bool changed;
		bool errorChanged;
		EnterWriteMutex();
		try {
			var words = ReadWordsForAdd();
			if (words.Add(normalized)) {
				_storage.WriteWords(Ordered(words));
			}

			changed = ReplaceWords(Freeze(words));
			errorChanged = ReplaceLoadError(null);
			SetWatcher();
		} catch (Exception ex) when (IsStorageFailure(ex)) {
			throw new SpellDictionaryException($"Could not update spell dictionary '{FilePath}': {ex.Message}", ex);
		} finally {
			_writeMutex.ReleaseMutex();
		}

		if (changed) {
			Changed?.Invoke();
		}
		if (errorChanged) {
			LoadErrorChanged?.Invoke(null);
		}
	}

	/// <summary>Reloads the backing file and raises <see cref="Changed"/> only when its normalized word set changed.</summary>
	public void Reload() {
		bool changed;
		try {
			changed = ReloadCore();
		} catch (SpellDictionaryException ex) {
			if (ReplaceLoadError(ex)) {
				LoadErrorChanged?.Invoke(ex);
			}
			throw;
		}

		bool errorChanged = ReplaceLoadError(null);
		if (changed) {
			Changed?.Invoke();
		}
		if (errorChanged) {
			LoadErrorChanged?.Invoke(null);
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		FileSystemWatcher? watcher;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			watcher = _watcher;
			_watcher = null;
		}

		if (watcher is not null) {
			watcher.EnableRaisingEvents = false;
			watcher.Changed -= OnFileEvent;
			watcher.Created -= OnFileEvent;
			watcher.Deleted -= OnFileEvent;
			watcher.Renamed -= OnFileEvent;
			watcher.Dispose();
		}

		_debounce?.Dispose();
		_writeMutex.Dispose();
	}

	private void LoadInitialSnapshot() {
		try {
			ReloadCore();
		} catch (SpellDictionaryException ex) {
			ReplaceLoadError(ex);
		}
	}

	private bool ReloadCore() {
		FrozenSet<string> words;
		try {
			words = Freeze(_storage.ReadWords());
		} catch (Exception ex) when (IsStorageFailure(ex)) {
			throw NewLoadFailure(ex);
		}

		return ReplaceWords(words);
	}

	private HashSet<string> ReadWordsForAdd() {
		try {
			return _storage.ReadWords();
		} catch (Exception ex) when (IsStorageFailure(ex)) {
			var error = NewLoadFailure(ex);
			if (ReplaceLoadError(error)) {
				LoadErrorChanged?.Invoke(error);
			}
			throw;
		}
	}

	private bool ReplaceWords(FrozenSet<string> words) {
		lock (_gate) {
			if (Words.SetEquals(words)) {
				return false;
			}

			Volatile.Write(ref _words, words);
			return true;
		}
	}

	private bool ReplaceLoadError(SpellDictionaryException? error) {
		lock (_gate) {
			if (SameError(_lastLoadError, error)) {
				return false;
			}

			_lastLoadError = error;
			return true;
		}
	}

	private void SetWatcher() {
		if (!_watch) {
			return;
		}

		string? directory;
		try {
			directory = _storage.WatchDirectory();
		} catch (Exception ex) when (IsStorageFailure(ex)) {
			if (LastLoadError is null) {
				var error = new SpellDictionaryException($"Could not watch spell dictionary '{FilePath}': {ex.Message}", ex);
				if (ReplaceLoadError(error)) {
					LoadErrorChanged?.Invoke(error);
				}
			}
			return;
		}

		if (string.IsNullOrEmpty(directory)) {
			return;
		}

		lock (_gate) {
			if (_watcher is not null || _disposed) {
				return;
			}

			_watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath)) {
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
				EnableRaisingEvents = true,
			};
			_watcher.Changed += OnFileEvent;
			_watcher.Created += OnFileEvent;
			_watcher.Deleted += OnFileEvent;
			_watcher.Renamed += OnFileEvent;
		}
	}

	private void OnFileEvent(object sender, FileSystemEventArgs e) {
		lock (_gate) {
			if (!_disposed) {
				_debounce?.Change(100, Timeout.Infinite);
			}
		}
	}

	private void OnDebounceElapsed(object? state) {
		try {
			Reload();
		} catch (SpellDictionaryException ex) {
			LoadFailed?.Invoke(ex);
		}
	}

	private static FrozenSet<string> Freeze(IEnumerable<string> words) => words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static IEnumerable<string> Ordered(IEnumerable<string> words) => words
		.OrderBy(word => word, StringComparer.OrdinalIgnoreCase)
		.ThenBy(word => word, StringComparer.Ordinal);

	private void EnterWriteMutex() {
		try {
			_writeMutex.WaitOne();
		} catch (AbandonedMutexException) {
			// The abandoned mutex is acquired; its prior owner exited during an update.
		}
	}

	private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException;

	private SpellDictionaryException NewLoadFailure(Exception ex) =>
		new($"Could not load spell dictionary '{FilePath}': {ex.Message}", ex);

	private static bool SameError(SpellDictionaryException? left, SpellDictionaryException? right) =>
		left is null ? right is null : right is not null && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

	private static string MutexName(string path) {
		string normalized = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
		byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
		return $"weavie-spelling-{Convert.ToHexString(bytes)}";
	}
}
