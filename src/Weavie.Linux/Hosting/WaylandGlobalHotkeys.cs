using Weavie.Core.Commands;

namespace Weavie.Linux.Hosting;

internal sealed class WaylandGlobalHotkeys : ILinuxGlobalHotkeyBackend {
	private readonly IGlobalShortcutsPortal _portal;
	private readonly SemaphoreSlim _applyGate = new(1, 1);
	private readonly object _stateGate = new();
	private ApplyOperation? _apply;
	private string? _sessionHandle;
	private IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> _desired =
		new Dictionary<string, (GlobalHotkey, PortalShortcut)>();
	private IReadOnlyDictionary<string, GlobalHotkey> _active = new Dictionary<string, GlobalHotkey>();
	private long _generation;
	private bool _disposed;

	internal WaylandGlobalHotkeys(IGlobalShortcutsPortal portal) {
		ArgumentNullException.ThrowIfNull(portal);
		_portal = portal;
		_portal.Activated += OnActivated;
		_portal.Invalidated += OnInvalidated;
		_portal.Log += OnLog;
	}

	public event Action<GlobalHotkey, string?>? Pressed;

	public event Action<string>? Log;

	public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) {
		ArgumentNullException.ThrowIfNull(hotkeys);
		if (_disposed) {
			return;
		}

		ScheduleReset(BuildDesired(hotkeys), force: false, forgetSession: false);
	}

	private void ScheduleReset(
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired,
		bool force,
		bool forgetSession) {
		var operation = new ApplyOperation();
		ApplyOperation? previous;
		long generation;
		lock (_stateGate) {
			if (_disposed || (!force && DesiredEquals(_desired, desired))) {
				operation.Cancellation.Dispose();
				return;
			}
			_desired = desired;
			if (forgetSession) {
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
			}
			previous = _apply;
			_apply = operation;
			generation = ++_generation;
			operation.Task = RunOperationAsync(previous, generation, desired, operation.Cancellation.Token);
		}
		if (previous is not null) {
			previous.Cancellation.Cancel();
			DisposeCancellationAfterCompletion(previous);
		}
	}

	public void Dispose() {
		ApplyOperation? operation;
		lock (_stateGate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			operation = _apply;
			_apply = null;
			operation?.Cancellation.Cancel();
		}

		_portal.Activated -= OnActivated;
		_portal.Invalidated -= OnInvalidated;
		_portal.Log -= OnLog;
		try {
			try {
				operation?.Task.GetAwaiter().GetResult();
			} catch (OperationCanceledException) {
			}

			string? session;
			lock (_stateGate) {
				session = _sessionHandle;
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
			}
			if (session is not null) {
				_portal.CloseSessionAsync(session).GetAwaiter().GetResult();
			}
		} finally {
			operation?.Cancellation.Dispose();
			_applyGate.Dispose();
			_portal.Dispose();
		}
	}

	private IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> BuildDesired(
		IReadOnlyList<GlobalHotkey> hotkeys) {
		var desired = new Dictionary<string, (GlobalHotkey, PortalShortcut)>(StringComparer.Ordinal);
		foreach (var hotkey in hotkeys) {
			if (!LinuxHotkeyMapping.TryPortalTrigger(hotkey, out string trigger)) {
				Log?.Invoke($"[hotkey] can't map '{hotkey.Chord}' to an XDG shortcut; skipping '{hotkey.Command}'.");
				continue;
			}

			string baseId = hotkey.Command.Replace('.', '-');
			string id = baseId;
			for (int suffix = 2; desired.ContainsKey(id); suffix++) {
				id = $"{baseId}-{suffix}";
			}
			string description = hotkey.Command == CoreCommands.ToggleWindow
				? "Toggle the Weavie window"
				: hotkey.Command;
			desired[id] = (hotkey, new PortalShortcut(id, description, trigger));
		}
		return desired;
	}

	private async Task ResetAsync(
		long generation,
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired,
		CancellationToken ct) {
		await _applyGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			string? previousSession;
			lock (_stateGate) {
				previousSession = _sessionHandle;
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
			}
			if (previousSession is not null) {
				await _portal.CloseSessionAsync(previousSession).ConfigureAwait(false);
			}
			ct.ThrowIfCancellationRequested();
			if (desired.Count == 0) {
				return;
			}

			var binding = await _portal.BindAsync(
				desired.Values.Select(value => value.Shortcut).ToArray(),
				ct).ConfigureAwait(false);
			bool stale;
			lock (_stateGate) {
				stale = ct.IsCancellationRequested || generation != _generation;
				if (!stale) {
					_sessionHandle = binding.SessionHandle;
					_active = desired
						.Where(pair => binding.ShortcutIds.Contains(pair.Key))
						.ToDictionary(pair => pair.Key, pair => pair.Value.Hotkey, StringComparer.Ordinal);
				}
			}
			if (stale) {
				await _portal.CloseSessionAsync(binding.SessionHandle).ConfigureAwait(false);
				ct.ThrowIfCancellationRequested();
				return;
			}

			foreach (var (id, value) in desired) {
				if (!binding.ShortcutIds.Contains(id)) {
					Log?.Invoke(
						$"[hotkey] the desktop did not bind '{value.Hotkey.Chord}'; '{value.Hotkey.Command}' is unavailable.");
				}
			}
		} finally {
			_applyGate.Release();
		}
	}

	private async Task ReportFailureAsync(Task task, CancellationToken ct) {
		try {
			await task.ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
		} catch (Exception ex) {
			Log?.Invoke($"[hotkey] Wayland global-shortcut registration failed: {ex.Message}");
		}
	}

	private async Task RunOperationAsync(
		ApplyOperation? previous,
		long generation,
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired,
		CancellationToken ct) {
		if (previous is not null) {
			await previous.Task.ConfigureAwait(false);
		}
		await ReportFailureAsync(ResetAsync(generation, desired, ct), ct).ConfigureAwait(false);
	}

	private void OnActivated(PortalActivation activation) {
		GlobalHotkey? hotkey;
		lock (_stateGate) {
			hotkey = string.Equals(activation.SessionHandle, _sessionHandle, StringComparison.Ordinal)
				&& _active.TryGetValue(activation.ShortcutId, out var active)
				? active
				: null;
		}
		if (hotkey is not null) {
			Pressed?.Invoke(hotkey, activation.ActivationToken);
		}
	}

	private void OnInvalidated() {
		lock (_stateGate) {
			if (_disposed) {
				return;
			}
			ScheduleReset(_desired, force: true, forgetSession: true);
		}
	}

	private void OnLog(string message) => Log?.Invoke(message);

	private static void DisposeCancellationAfterCompletion(ApplyOperation operation) {
		if (operation.Task.IsCompleted) {
			operation.Cancellation.Dispose();
			return;
		}
		_ = operation.Task.ContinueWith(
			static (_, state) => ((CancellationTokenSource)state!).Dispose(),
			operation.Cancellation,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private static bool DesiredEquals(
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> left,
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> right) =>
		left.Count == right.Count
		&& left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);

	private sealed class ApplyOperation {
		internal CancellationTokenSource Cancellation { get; } = new();
		internal Task Task { get; set; } = Task.CompletedTask;
	}
}
