using System.Runtime.InteropServices;
using Weavie.Core.Commands;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// X11 global hotkeys over a private connection to the display. The grabs have to live on the connection that
/// reads their key events, and GTK 4 exposes no hook into GDK's, so this owns both — watching the connection's
/// descriptor on the main loop rather than polling it.
/// </summary>
internal sealed class X11GlobalHotkeys : ILinuxGlobalHotkeyBackend {
	private const uint BaseModifierMask =
		X11.ShiftMask | X11.LockMask | X11.ControlMask | X11.Mod1Mask | X11.Mod4Mask;

	private readonly IntPtr _display;
	private readonly nuint _root;
	private readonly uint _numLockMask;
	private readonly IntPtr _eventBuffer;
	private readonly uint _connectionWatch;
	private readonly Dictionary<(uint KeyCode, uint State), GlobalHotkey> _registered = [];
	private readonly HashSet<(uint KeyCode, uint State)> _down = [];

	// Kept alive: native holds bare function pointers to these.
	private readonly UnixFdSourceFunc _onConnectionReadable;
	private readonly X11ErrorHandler _onError;

	private byte _grabError;
	private bool _disposed;

	internal X11GlobalHotkeys() {
		_display = X11.XOpenDisplay(null);
		if (_display == IntPtr.Zero) {
			throw new InvalidOperationException("Could not open an X11 connection for global hotkeys.");
		}

		_root = X11.XRootWindow(_display, X11.XDefaultScreen(_display));
		_numLockMask = X11.XkbKeysymToModifiers(_display, Gdk.gdk_keyval_from_name("Num_Lock"));
		_eventBuffer = Marshal.AllocHGlobal(X11.EventSize);
		_onError = OnError;
		_onConnectionReadable = OnConnectionReadable;
		_connectionWatch = GLib.g_unix_fd_add_full(
			GLib.PriorityDefault,
			X11.XConnectionNumber(_display),
			GLib.IoIn,
			Marshal.GetFunctionPointerForDelegate(_onConnectionReadable),
			IntPtr.Zero,
			IntPtr.Zero);
	}

	public event Action<GlobalHotkey, string?>? Pressed;

	public event Action<string>? Log;

	public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) {
		ArgumentNullException.ThrowIfNull(hotkeys);
		if (_disposed) {
			return;
		}

		UnregisterAll();
		foreach (var hotkey in hotkeys) {
			Register(hotkey);
		}
	}

	public void Dispose() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		UnregisterAll();
		_ = GLib.g_source_remove(_connectionWatch);
		_ = X11.XCloseDisplay(_display);
		Marshal.FreeHGlobal(_eventBuffer);
	}

	private void Register(GlobalHotkey hotkey) {
		if (!LinuxHotkeyMapping.TryKeyval(hotkey.Key, out uint keyval)) {
			Log?.Invoke($"[hotkey] can't map '{hotkey.Chord}' to an X11 keysym; skipping '{hotkey.Command}'.");
			return;
		}

		uint keyCode = X11.XKeysymToKeycode(_display, keyval);
		if (keyCode == 0) {
			Log?.Invoke($"[hotkey] X11 has no keycode for '{hotkey.Key}'; skipping '{hotkey.Command}'.");
			return;
		}

		uint[] variants = ModifierVariants(LinuxHotkeyMapping.X11Modifiers(hotkey.Modifiers));
		if (variants.Any(state => _registered.ContainsKey((keyCode, state)))) {
			Log?.Invoke($"[hotkey] duplicate Linux global binding '{hotkey.Chord}'; skipping '{hotkey.Command}'.");
			return;
		}

		if (Grab(keyCode, variants) is { } error) {
			foreach (uint state in variants) {
				X11.XUngrabKey(_display, (int)keyCode, state, _root);
			}
			_ = X11.XSync(_display, discard: 0);
			Log?.Invoke(
				$"[hotkey] XGrabKey failed for '{hotkey.Chord}' (X11 error {error}); another application may own it.");
			return;
		}

		foreach (uint state in variants) {
			_registered[(keyCode, state)] = hotkey;
		}
	}

	// Xlib's default error handler exits the process, so the grabs run under one that only records the failure.
	// The handler is global to Xlib, so it is restored as soon as this connection has been synced.
	private byte? Grab(uint keyCode, uint[] variants) {
		_grabError = 0;
		IntPtr previous = X11.XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_onError));
		try {
			foreach (uint state in variants) {
				X11.XGrabKey(
					_display, (int)keyCode, state, _root, ownerEvents: 0, X11.GrabModeAsync, X11.GrabModeAsync);
			}
			_ = X11.XSync(_display, discard: 0);
		} finally {
			_ = X11.XSetErrorHandler(previous);
		}

		return _grabError == 0 ? null : _grabError;
	}

	private int OnError(IntPtr display, IntPtr error) {
		var failure = Marshal.PtrToStructure<X11.ErrorEvent>(error);
		if (failure._display == _display) {
			_grabError = failure._errorCode;
		}

		return 0;
	}

	private uint[] ModifierVariants(uint modifiers) =>
		_numLockMask == 0
			? [modifiers, modifiers | X11.LockMask]
			: [
				modifiers,
				modifiers | X11.LockMask,
				modifiers | _numLockMask,
				modifiers | X11.LockMask | _numLockMask,
			];

	private int OnConnectionReadable(int fd, int condition, IntPtr userData) {
		while (!_disposed && X11.XPending(_display) > 0) {
			_ = X11.XNextEvent(_display, _eventBuffer);
			Deliver(Marshal.PtrToStructure<X11.KeyEvent>(_eventBuffer));
		}

		return 1; // G_SOURCE_CONTINUE — keep watching the connection.
	}

	private void Deliver(X11.KeyEvent key) {
		if (key._type == X11.KeyRelease) {
			_ = _down.RemoveWhere(pressed => pressed.KeyCode == key._keyCode);
			return;
		}

		if (key._type != X11.KeyPress) {
			return;
		}

		var id = (key._keyCode, key._state & (BaseModifierMask | _numLockMask));
		if (_registered.TryGetValue(id, out var hotkey) && _down.Add(id)) {
			Pressed?.Invoke(hotkey, null);
		}
	}

	private void UnregisterAll() {
		foreach (var (keyCode, state) in _registered.Keys) {
			X11.XUngrabKey(_display, (int)keyCode, state, _root);
		}
		if (_registered.Count > 0) {
			_ = X11.XSync(_display, discard: 0);
		}
		_registered.Clear();
		_down.Clear();
	}
}
