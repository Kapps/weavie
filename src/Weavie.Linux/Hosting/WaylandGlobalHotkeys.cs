using Weavie.Core.Commands;

namespace Weavie.Linux.Hosting;

internal sealed class WaylandGlobalHotkeys : ILinuxGlobalHotkeyBackend {
	private readonly IGlobalShortcutsPortal _portal;
	private readonly object _gate = new();
	private IReadOnlyDictionary<string, (GlobalHotkey Hotkey, PortalShortcut Shortcut)> _desired =
		new Dictionary<string, (GlobalHotkey, PortalShortcut)>();
	private IReadOnlyDictionary<string, GlobalHotkey> _active = new Dictionary<string, GlobalHotkey>();
	private readonly List<PortalActivation> _pendingActivations = [];
	private Task? _worker;
	private string? _sessionHandle;
	private long _revision;
	private bool _binding;
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
		_portal.Dispose();
		try {
			worker?.GetAwaiter().GetResult();
		} finally {
			lock (_gate) {
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
				_pendingActivations.Clear();
			}
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
			_worker ??= RunApplyLoopAsync();
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
					_worker = null;
					return;
				}
				revision = _revision;
				desired = _desired;
				previousSession = _sessionHandle;
				_sessionHandle = null;
				_active = new Dictionary<string, GlobalHotkey>();
				_pendingActivations.Clear();
				_binding = desired.Count > 0;
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
				var pending = new List<(GlobalHotkey Hotkey, string? Token)>();
				lock (_gate) {
					stale = _disposed || revision != _revision;
					_binding = false;
					if (!stale && binding is not null) {
						_sessionHandle = binding.SessionHandle;
						_active = desired
							.Where(pair => binding.ShortcutIds.Contains(pair.Key))
							.ToDictionary(pair => pair.Key, pair => pair.Value.Hotkey, StringComparer.Ordinal);
						foreach (var activation in _pendingActivations) {
							if (string.Equals(activation.SessionHandle, binding.SessionHandle, StringComparison.Ordinal)
								&& _active.TryGetValue(activation.ShortcutId, out var hotkey)) {
								pending.Add((hotkey, activation.ActivationToken));
							}
						}
					}
					_pendingActivations.Clear();
				}
				if (stale && binding is not null) {
					await _portal.CloseSessionAsync(binding.SessionHandle).ConfigureAwait(false);
				}
				if (!stale && binding is not null) {
					ReportOmitted(desired, binding.ShortcutIds);
					foreach (var (hotkey, token) in pending) {
						Pressed?.Invoke(hotkey, token);
					}
				}
			} catch (Exception ex) {
				lock (_gate) {
					_binding = false;
					_pendingActivations.Clear();
				}
				Log?.Invoke($"[hotkey] Wayland global-shortcut registration failed: {ex.Message}");
			}

			lock (_gate) {
				if (_disposed || revision == _revision) {
					_worker = null;
					return;
				}
			}
		}
	}

	private void OnActivated(PortalActivation activation) {
		GlobalHotkey? hotkey;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			hotkey = string.Equals(activation.SessionHandle, _sessionHandle, StringComparison.Ordinal)
				&& _active.TryGetValue(activation.ShortcutId, out var active)
				? active
				: null;
			if (hotkey is null && _binding) {
				_pendingActivations.Add(activation);
			}
		}
		if (hotkey is not null) {
			Pressed?.Invoke(hotkey, activation.ActivationToken);
		}
	}

	private void OnInvalidated() {
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_sessionHandle = null;
			_active = new Dictionary<string, GlobalHotkey>();
			_revision++;
			_worker ??= RunApplyLoopAsync();
		}
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
