namespace Weavie.Core.Spelling;

internal sealed class DictionaryWatcher : IDisposable {
	private readonly Lock _gate = new();
	private readonly DictionaryStorage _storage;
	private readonly Action<SpellDictionaryException?> _changed;
	private readonly bool _enabled;
	private readonly Timer? _debounce;

	private List<Registration> _registrations = [];
	private Exception? _pendingError;
	private bool _disposed;

	internal DictionaryWatcher(
		DictionaryStorage storage,
		bool enabled,
		Action<SpellDictionaryException?> changed) {
		_storage = storage;
		_changed = changed;
		_enabled = enabled;
		_debounce = enabled ? new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite) : null;
	}

	internal SpellDictionaryException? Refresh() {
		if (!_enabled || IsDisposed()) {
			return null;
		}

		Exception? failure = null;
		IReadOnlyList<DictionaryWatchTarget> targets;
		try {
			targets = _storage.WatchTargets();
		} catch (Exception ex) when (IsWatcherFailure(ex)) {
			failure = ex;
			try {
				targets = [_storage.RecoveryWatchTarget()];
			} catch (Exception recoveryError) when (IsWatcherFailure(recoveryError)) {
				failure = Combine(failure, recoveryError);
				targets = [];
			}
		}

		if (targets.Count == 0) {
			return NewFailure(failure ?? new IOException("No dictionary watch target could be resolved."));
		}

		lock (_gate) {
			if (SameTargets(_registrations, targets)) {
				return failure is null ? null : NewFailure(failure);
			}
		}

		var replacements = new List<Registration>();
		foreach (var target in targets) {
			if (ContainsTarget(replacements, target)) {
				continue;
			}

			if (TryCreate(target, out var registration, out var createError)) {
				replacements.Add(registration);
				continue;
			}

			failure = Combine(failure, createError!);
			try {
				var recovery = DictionaryStorage.ParentWatchTarget(target.DirectoryPath);
				if (!SameTarget(recovery, target) && !ContainsTarget(replacements, recovery)) {
					if (TryCreate(recovery, out registration, out var recoveryError)) {
						replacements.Add(registration);
					} else {
						failure = Combine(failure, recoveryError!);
					}
				}
			} catch (Exception recoveryError) when (IsWatcherFailure(recoveryError)) {
				failure = Combine(failure, recoveryError);
			}
		}

		if (replacements.Count == 0 && targets.Count > 0) {
			return NewFailure(failure ?? new IOException("No dictionary watch target could be opened."));
		}

		List<Registration>? previous;
		lock (_gate) {
			if (_disposed) {
				previous = null;
			} else {
				previous = _registrations;
				_registrations = replacements;
			}
		}

		if (previous is null) {
			DisposeAll(replacements);
		} else {
			DisposeAll(previous);
		}

		return failure is null ? null : NewFailure(failure);
	}

	public void Dispose() {
		List<Registration> registrations;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			registrations = _registrations;
			_registrations = [];
			_pendingError = null;
		}

		DisposeAll(registrations);
		_debounce?.Dispose();
	}

	private bool TryCreate(
		DictionaryWatchTarget target,
		out Registration registration,
		out Exception? error) {
		FileSystemWatcher? watcher = null;
		try {
			watcher = new FileSystemWatcher(target.DirectoryPath, Path.GetFileName(target.EntryPath)) {
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
					| NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.Security,
				IncludeSubdirectories = false,
			};
			watcher.Changed += OnFileEvent;
			watcher.Created += OnFileEvent;
			watcher.Deleted += OnFileEvent;
			watcher.Renamed += OnFileEvent;
			watcher.Error += OnWatcherError;
			watcher.EnableRaisingEvents = true;
			registration = new Registration(watcher, target);
			error = null;
			return true;
		} catch (Exception ex) when (IsWatcherFailure(ex)) {
			DisposeWatcher(watcher);
			registration = default;
			error = ex;
			return false;
		}
	}

	private void OnFileEvent(object sender, FileSystemEventArgs e) {
		DictionaryWatchTarget? target = null;
		lock (_gate) {
			if (!_disposed) {
				foreach (var registration in _registrations) {
					if (ReferenceEquals(registration.Watcher, sender)) {
						target = registration.Target;
						break;
					}
				}
			}
		}

		if (target is { } current && EventMatchesTarget(e, current.EntryPath)) {
			ScheduleReload();
		}
	}

	private void OnWatcherError(object sender, ErrorEventArgs e) {
		Registration? failed = null;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			int index = _registrations.FindIndex(item => ReferenceEquals(item.Watcher, sender));
			if (index >= 0) {
				failed = _registrations[index];
				_registrations.RemoveAt(index);
				_pendingError = Combine(_pendingError, e.GetException());
			}
		}

		if (failed is not null) {
			DisposeWatcher(failed.Value.Watcher);
			ScheduleReload();
		}
	}

	private void OnDebounceElapsed(object? state) {
		Exception? error;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			error = _pendingError;
			_pendingError = null;
		}

		_changed(error is null ? null : NewFailure(error));
	}

	private void ScheduleReload() {
		lock (_gate) {
			if (!_disposed) {
				_debounce?.Change(100, Timeout.Infinite);
			}
		}
	}

	private bool IsDisposed() {
		lock (_gate) {
			return _disposed;
		}
	}

	private void DisposeAll(IEnumerable<Registration> registrations) {
		foreach (var registration in registrations) {
			DisposeWatcher(registration.Watcher);
		}
	}

	private void DisposeWatcher(FileSystemWatcher? watcher) {
		if (watcher is null) {
			return;
		}

		watcher.Changed -= OnFileEvent;
		watcher.Created -= OnFileEvent;
		watcher.Deleted -= OnFileEvent;
		watcher.Renamed -= OnFileEvent;
		watcher.Error -= OnWatcherError;
		watcher.EnableRaisingEvents = false;
		watcher.Dispose();
	}

	private static bool EventMatchesTarget(FileSystemEventArgs e, string entryPath) =>
		DictionaryStorage.PathsEqual(e.FullPath, entryPath)
		|| (e is RenamedEventArgs renamed && DictionaryStorage.PathsEqual(renamed.OldFullPath, entryPath));

	private static bool SameTargets(
		IReadOnlyCollection<Registration> registrations,
		IReadOnlyCollection<DictionaryWatchTarget> targets) =>
		registrations.Count == targets.Count
		&& registrations.All(registration => targets.Any(target => SameTarget(registration.Target, target)));

	private static bool ContainsTarget(IEnumerable<Registration> registrations, DictionaryWatchTarget target) =>
		registrations.Any(registration => SameTarget(registration.Target, target));

	private static bool SameTarget(DictionaryWatchTarget left, DictionaryWatchTarget right) =>
		DictionaryStorage.PathsEqual(left.DirectoryPath, right.DirectoryPath)
		&& DictionaryStorage.PathsEqual(left.EntryPath, right.EntryPath);

	private static bool IsWatcherFailure(Exception ex) =>
		ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException;

	private static Exception Combine(Exception? first, Exception next) =>
		first is null ? next : new AggregateException(first, next);

	private SpellDictionaryException NewFailure(Exception ex) =>
		new($"Could not watch spell dictionary '{_storage.FilePath}': {ex.Message}", ex);

	private readonly record struct Registration(
		FileSystemWatcher Watcher,
		DictionaryWatchTarget Target);
}
