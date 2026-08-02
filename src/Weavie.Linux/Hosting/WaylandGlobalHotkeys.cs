using Weavie.Core.Commands;

namespace Weavie.Linux.Hosting;

internal sealed class WaylandGlobalHotkeys : ILinuxGlobalHotkeyBackend {
	private readonly IGlobalShortcutsPortal _portal;
	private readonly object _gate = new();
	private IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> _desired =
		new Dictionary<string, (GlobalHotkey, PortalShortcut)>();
	private IReadOnlyDictionary<string, GlobalHotkey> _active = new Dictionary<string, GlobalHotkey>();
	private Task? _worker;
	private string? _sessionHandle;
	private long _revision;
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
		Schedule(BuildDesired(hotkeys), force: false);
	}

	public void Dispose() {
		Task? worker;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			_revision++;
			worker = _worker;
		}
		_portal.Activated -= OnActivated;
		_portal.Invalidated -= OnInvalidated;
		_portal.Log -= OnLog;
		try {
			worker?.GetAwaiter().GetResult();
			string? session;
			lock (_gate) {
				session = _sessionHandle;
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
			}
			if (session is not null) {
				_portal.CloseSessionAsync(session).GetAwaiter().GetResult();
			}
		} finally {
			_portal.Dispose();
		}
	}

	private void Schedule(
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired,
		bool force) {
		lock (_gate) {
			if (_disposed || (!force && DesiredEquals(_desired, desired))) {
				return;
			}
			_desired = desired;
			_revision++;
			if (_worker is null || _worker.IsCompleted) {
				_worker = RunApplyLoopAsync();
			}
		}
	}

	private async Task RunApplyLoopAsync() {
		await Task.Yield();
		while (true) {
			long revision;
			string? previousSession;
			IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired;
			lock (_gate) {
				if (_disposed) {
					return;
				}
				revision = _revision;
				desired = _desired;
				previousSession = _sessionHandle;
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
			}

			try {
				if (previousSession is not null) {
					await _portal.CloseSessionAsync(previousSession).ConfigureAwait(false);
				}
				var binding = desired.Count == 0
					? null
					: await _portal.BindAsync(desired.Values.Select(value => value.Shortcut).ToArray())
						.ConfigureAwait(false);
				bool stale;
				lock (_gate) {
					stale = _disposed || revision != _revision;
					if (!stale && binding is not null) {
						_sessionHandle = binding.SessionHandle;
						_active = desired
							.Where(pair => binding.ShortcutIds.Contains(pair.Key))
							.ToDictionary(pair => pair.Key, pair => pair.Value.Hotkey, StringComparer.Ordinal);
					}
				}
				if (stale && binding is not null) {
					await _portal.CloseSessionAsync(binding.SessionHandle).ConfigureAwait(false);
				}
				if (!stale && binding is not null) {
					ReportOmitted(desired, binding.ShortcutIds);
				}
			} catch (Exception ex) {
				Log?.Invoke($"[hotkey] Wayland global-shortcut registration failed: {ex.Message}");
			}

			lock (_gate) {
				if (_disposed || revision == _revision) {
					return;
				}
			}
		}
	}

	private void OnActivated(PortalActivation activation) {
		GlobalHotkey? hotkey;
		lock (_gate) {
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
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_sessionHandle = null;
			_active = new Dictionary<string, GlobalHotkey>();
			desired = _desired;
		}
		Schedule(desired, force: true);
	}

	private void OnLog(string message) => Log?.Invoke(message);

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

	private void ReportOmitted(
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> desired,
		IReadOnlySet<string> accepted) {
		foreach (var (id, value) in desired) {
			if (!accepted.Contains(id)) {
				Log?.Invoke(
					$"[hotkey] the desktop did not bind '{value.Hotkey.Chord}'; '{value.Hotkey.Command}' is unavailable.");
			}
		}
	}

	private static bool DesiredEquals(
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> left,
		IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> right) =>
		left.Count == right.Count
		&& left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);
}
