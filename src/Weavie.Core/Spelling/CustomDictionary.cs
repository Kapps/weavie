using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Weavie.Core.Spelling;

/// <summary>A normalized, file-backed custom dictionary shared by the Project or User spelling scope.</summary>
public sealed class CustomDictionary : IDisposable {
	private static readonly FrozenSet<string> EmptyWords = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private readonly Lock _gate = new();
	private readonly Lock _operationGate = new();
	private readonly DictionaryStorage _storage;
	private readonly DictionaryWatcher _watcher;
	private readonly Mutex _writeMutex;

	private FrozenSet<string> _words = EmptyWords;
	private SpellDictionaryException? _lastLoadError;
	private long _revision;
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
		_watcher = new DictionaryWatcher(storage, enableWatcher, OnWatchedFileChanged);
		try {
			LoadInitialSnapshot();
			RefreshWatcher();
		} catch {
			_watcher.Dispose();
			_writeMutex.Dispose();
			throw;
		}
	}

	/// <summary>Raised after the in-memory word snapshot changes.</summary>
	public event Action<CustomDictionary>? Changed;

	/// <summary>Raised when the current load error appears or is repaired. <c>null</c> means the file is valid again.</summary>
	public event Action<CustomDictionary, SpellDictionaryException?>? LoadErrorChanged;

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

	/// <summary>A monotonic version of the active word snapshot.</summary>
	public long Revision => Volatile.Read(ref _revision);

	/// <summary>Returns whether <paramref name="word"/> is present after NFC and apostrophe normalization.</summary>
	public bool Contains(string word) => SpellWord.TryNormalize(word, out string normalized) && Words.Contains(normalized);

	/// <summary>Adds <paramref name="word"/> if absent, creating the file and parent directory only for this write.</summary>
	public void Add(string word) {
		string normalized = SpellWord.RequireNormalized(word, nameof(word));
		lock (_operationGate) {
			ThrowIfDisposed();
			try {
				AddCore(normalized);
			} finally {
				RefreshWatcher();
			}
		}
	}

	private void AddCore(string normalized) {
		bool changed = false;
		bool errorChanged = false;
		bool readComplete = false;
		Exception? failure = null;
		EnterWriteMutex();
		try {
			var words = _storage.ReadWords();
			readComplete = true;
			if (words.Add(normalized)) {
				_storage.WriteWords(Ordered(words));
			}

			changed = ReplaceWords(Freeze(words));
			errorChanged = ReplaceLoadError(null);
		} catch (Exception ex) when (IsStorageFailure(ex)) {
			failure = ex;
		} finally {
			_writeMutex.ReleaseMutex();
		}

		if (failure is not null) {
			if (!readComplete) {
				PublishLoadError(NewLoadFailure(failure));
			}
			throw new SpellDictionaryException($"Could not update spell dictionary '{FilePath}': {failure.Message}", failure);
		}

		if (changed) {
			Changed?.Invoke(this);
		}
		if (errorChanged) {
			LoadErrorChanged?.Invoke(this, null);
		}
	}

	/// <summary>Reloads the backing file and raises <see cref="Changed"/> only when its normalized word set changed.</summary>
	public void Reload() {
		lock (_operationGate) {
			ThrowIfDisposed();
			try {
				ReloadAndPublish();
			} finally {
				RefreshWatcher();
			}
		}
	}

	private void ReloadAndPublish() {
		bool changed;
		try {
			changed = ReloadCore();
		} catch (SpellDictionaryException ex) {
			if (ReplaceLoadError(ex)) {
				LoadErrorChanged?.Invoke(this, ex);
			}
			throw;
		}

		bool errorChanged = ReplaceLoadError(null);
		if (changed) {
			Changed?.Invoke(this);
		}
		if (errorChanged) {
			LoadErrorChanged?.Invoke(this, null);
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_operationGate) {
			lock (_gate) {
				if (_disposed) {
					return;
				}

				_disposed = true;
			}

			_watcher.Dispose();
			_writeMutex.Dispose();
		}
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

	private bool ReplaceWords(FrozenSet<string> words) {
		lock (_gate) {
			if (Words.SetEquals(words)) {
				return false;
			}

			Volatile.Write(ref _words, words);
			Interlocked.Increment(ref _revision);
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

	private void OnWatchedFileChanged(SpellDictionaryException? watchError) {
		lock (_operationGate) {
			if (IsDisposed()) {
				return;
			}

			if (watchError is not null) {
				PublishLoadError(watchError);
			}

			try {
				ReloadAndPublish();
			} catch (SpellDictionaryException) {
				// Reload already published LoadErrorChanged and retained the last good snapshot.
			} finally {
				RefreshWatcher();
			}
		}
	}

	private void RefreshWatcher() {
		var error = _watcher.Refresh();
		if (error is not null) {
			PublishLoadError(error);
		}
	}

	private bool IsDisposed() {
		lock (_gate) {
			return _disposed;
		}
	}

	private void ThrowIfDisposed() {
		if (IsDisposed()) {
			throw new ObjectDisposedException(nameof(CustomDictionary));
		}
	}

	private void PublishLoadError(SpellDictionaryException error) {
		if (ReplaceLoadError(error)) {
			LoadErrorChanged?.Invoke(this, error);
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
