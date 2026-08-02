using System.Runtime.InteropServices;
using Weavie.Core.Commands;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

internal sealed class X11GlobalHotkeys : ILinuxGlobalHotkeyBackend {
	private const uint BaseModifierMask =
		X11.ShiftMask | X11.LockMask | X11.ControlMask | X11.Mod1Mask | X11.Mod4Mask;

	private readonly IntPtr _gdkDisplay;
	private readonly IntPtr _display;
	private readonly nuint _root;
	private readonly uint _numLockMask;
	private readonly NativeEventFilter _filter;
	private readonly IntPtr _filterPointer;
	private readonly Dictionary<(uint KeyCode, uint State), GlobalHotkey> _registered = [];
	private readonly HashSet<(uint KeyCode, uint State)> _down = [];
	private bool _disposed;

	internal X11GlobalHotkeys(IntPtr gdkDisplay) {
		_gdkDisplay = gdkDisplay;
		_display = Gdk.gdk_x11_display_get_xdisplay(gdkDisplay);
		if (_display == IntPtr.Zero) {
			throw new InvalidOperationException("GDK did not expose its X11 display connection.");
		}

		_root = X11.XRootWindow(_display, X11.XDefaultScreen(_display));
		uint numLockKey = X11.XKeysymToKeycode(_display, X11.XStringToKeysym("Num_Lock"));
		_numLockMask = X11.FindModifierMask(_display, numLockKey);
		_filter = OnNativeEvent;
		_filterPointer = Marshal.GetFunctionPointerForDelegate(_filter);
		Gdk.gdk_window_add_filter(IntPtr.Zero, _filterPointer, IntPtr.Zero);
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
		Gdk.gdk_window_remove_filter(IntPtr.Zero, _filterPointer, IntPtr.Zero);
	}

	private void Register(GlobalHotkey hotkey) {
		if (!LinuxHotkeyMapping.TryGetKeyName(hotkey.Key, out string keyName)) {
			Log?.Invoke($"[hotkey] can't map '{hotkey.Chord}' to an X11 keysym; skipping '{hotkey.Command}'.");
			return;
		}

		uint keyCode = X11.XKeysymToKeycode(_display, X11.XStringToKeysym(keyName));
		if (keyCode == 0) {
			Log?.Invoke($"[hotkey] X11 has no keycode for '{keyName}'; skipping '{hotkey.Command}'.");
			return;
		}

		uint modifiers = LinuxHotkeyMapping.X11Modifiers(hotkey.Modifiers);
		uint[] variants = ModifierVariants(modifiers);
		if (variants.Any(state => _registered.ContainsKey((keyCode, state)))) {
			Log?.Invoke($"[hotkey] duplicate Linux global binding '{hotkey.Chord}'; skipping '{hotkey.Command}'.");
			return;
		}

		Gdk.gdk_x11_display_error_trap_push(_gdkDisplay);
		foreach (uint state in variants) {
			X11.XGrabKey(
				_display,
				(int)keyCode,
				state,
				_root,
				ownerEvents: 0,
				X11.GrabModeAsync,
				X11.GrabModeAsync);
		}
		_ = X11.XSync(_display, discard: 0);
		int error = Gdk.gdk_x11_display_error_trap_pop(_gdkDisplay);
		if (error != 0) {
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

	private uint[] ModifierVariants(uint modifiers) =>
		_numLockMask == 0
			? [modifiers, modifiers | X11.LockMask]
			: [
				modifiers,
				modifiers | X11.LockMask,
				modifiers | _numLockMask,
				modifiers | X11.LockMask | _numLockMask,
			];

	private int OnNativeEvent(IntPtr nativeEvent, IntPtr gdkEvent, IntPtr userData) {
		_ = gdkEvent;
		_ = userData;
		var key = Marshal.PtrToStructure<X11.KeyEvent>(nativeEvent);
		if (key._type is not (X11.KeyPress or X11.KeyRelease)) {
			return Gdk.FilterContinue;
		}
		if (key._type == X11.KeyRelease) {
			int removed = _down.RemoveWhere(pressed => pressed.KeyCode == key._keyCode);
			return removed > 0 ? Gdk.FilterRemove : Gdk.FilterContinue;
		}

		uint state = key._state & (BaseModifierMask | _numLockMask);
		var id = (key._keyCode, state);
		if (!_registered.TryGetValue(id, out var hotkey)) {
			return Gdk.FilterContinue;
		}

		if (_down.Add(id)) {
			Pressed?.Invoke(hotkey, null);
		}

		return Gdk.FilterRemove;
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
