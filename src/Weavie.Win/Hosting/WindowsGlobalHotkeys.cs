using System.Runtime.InteropServices;
using Weavie.Core.Commands;

namespace Weavie.Win.Hosting;

/// <summary>
/// The Windows <see cref="IGlobalHotkeyRegistrar"/>: registers system-wide hotkeys via <c>RegisterHotKey</c>
/// and receives <c>WM_HOTKEY</c> on a hidden message-only window. Registrations and the message pump must
/// share a thread, so this is created on the UI thread and marshals <see cref="Apply"/> back onto it.
/// </summary>
internal sealed class WindowsGlobalHotkeys : NativeWindow, IGlobalHotkeyRegistrar {
	private const int WmHotkey = 0x0312;
	private static readonly IntPtr HwndMessage = new(-3); // HWND_MESSAGE — a message-only window.

	[Flags]
	private enum FsModifiers : uint {
		Alt = 0x0001,
		Control = 0x0002,
		Shift = 0x0004,
		Win = 0x0008,
		NoRepeat = 0x4000, // don't re-fire while the chord is held down.
	}

	private readonly SynchronizationContext _ui;
	private readonly object _gate = new();

	// _registered is UI-thread-only (no lock); _desired/_disposed cross threads (Apply/Dispose from the watcher).
	private readonly Dictionary<int, GlobalHotkey> _registered = [];
	private IReadOnlyList<GlobalHotkey> _desired = [];
	private bool _disposed;

	/// <summary>Creates the message-only window on the current (UI) thread; throws if there's no UI SynchronizationContext.</summary>
	public WindowsGlobalHotkeys() {
		_ui = SynchronizationContext.Current
			?? throw new InvalidOperationException(
				"WindowsGlobalHotkeys must be constructed on the WinForms UI thread (after a control exists).");
		CreateHandle(new CreateParams { Parent = HwndMessage });
	}

	/// <inheritdoc/>
	public event Action<GlobalHotkey>? Pressed;

	/// <inheritdoc/>
	public event Action<string>? Log;

	/// <inheritdoc/>
	public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) {
		ArgumentNullException.ThrowIfNull(hotkeys);
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_desired = hotkeys;
		}

		RunOnUi(ApplyOnUiThread);
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
		}

		RunOnUi(() => {
			UnregisterAll();
			if (Handle != IntPtr.Zero) {
				DestroyHandle();
			}
		});
	}

	/// <inheritdoc/>
	protected override void WndProc(ref Message m) {
		if (m.Msg == WmHotkey) {
			if (_registered.TryGetValue(m.WParam.ToInt32(), out var hotkey)) {
				Pressed?.Invoke(hotkey);
			}

			return;
		}

		base.WndProc(ref m);
	}

	// Re-register the full desired set (tiny): cleanly handles add/remove/rebind. UI thread only.
	private void ApplyOnUiThread() {
		IReadOnlyList<GlobalHotkey> desired;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			desired = _desired;
		}

		UnregisterAll();

		int nextId = 1;
		foreach (var hotkey in desired) {
			if (!TryMap(hotkey, out uint modifiers, out uint vk)) {
				Log?.Invoke($"[hotkey] can't map '{hotkey.Chord}' to a Windows key; skipping global binding for '{hotkey.Command}'.");
				continue;
			}

			int id = nextId++;
			if (RegisterHotKey(Handle, id, modifiers, vk)) {
				_registered[id] = hotkey;
			} else {
				Log?.Invoke(
					$"[hotkey] RegisterHotKey failed for '{hotkey.Chord}' (Win32 error {Marshal.GetLastWin32Error()}); "
					+ "another application may already own that combination.");
			}
		}
	}

	private void UnregisterAll() {
		foreach (int id in _registered.Keys.ToArray()) {
			UnregisterHotKey(Handle, id);
		}

		_registered.Clear();
	}

	// Inline on the UI thread, else marshals — keeps Apply()'s effect synchronous for UI-thread callers (Post defers).
	private void RunOnUi(Action action) {
		if (SynchronizationContext.Current == _ui) {
			action();
		} else {
			_ui.Post(_ => action(), null);
		}
	}

	private static bool TryMap(GlobalHotkey hotkey, out uint fsModifiers, out uint vk) {
		var modifiers = FsModifiers.NoRepeat;
		var m = hotkey.Modifiers;
		// $mod resolves to Ctrl on Windows (Cmd on macOS — see the Mac registrar).
		if (m.HasFlag(HotkeyModifiers.Ctrl) || m.HasFlag(HotkeyModifiers.Mod)) {
			modifiers |= FsModifiers.Control;
		}

		if (m.HasFlag(HotkeyModifiers.Shift)) {
			modifiers |= FsModifiers.Shift;
		}

		if (m.HasFlag(HotkeyModifiers.Alt)) {
			modifiers |= FsModifiers.Alt;
		}

		if (m.HasFlag(HotkeyModifiers.Meta)) {
			modifiers |= FsModifiers.Win;
		}

		fsModifiers = (uint)modifiers;
		return TryMapKey(hotkey.Key, out vk);
	}

	// Maps a normalized key token to a Win32 virtual-key code; any other single char via VkKeyScan (layout-aware).
	private static bool TryMapKey(string key, out uint vk) {
		vk = 0;
		if (string.IsNullOrEmpty(key)) {
			return false;
		}

		switch (key) {
			case "space": vk = 0x20; return true;
			case "enter": case "return": vk = 0x0D; return true;
			case "tab": vk = 0x09; return true;
			case "escape": case "esc": vk = 0x1B; return true;
			case "backspace": vk = 0x08; return true;
			case "delete": case "del": vk = 0x2E; return true;
			case "insert": vk = 0x2D; return true;
			case "home": vk = 0x24; return true;
			case "end": vk = 0x23; return true;
			case "pageup": vk = 0x21; return true;
			case "pagedown": vk = 0x22; return true;
			case "up": vk = 0x26; return true;
			case "down": vk = 0x28; return true;
			case "left": vk = 0x25; return true;
			case "right": vk = 0x27; return true;
		}

		if (key.Length >= 2 && key[0] == 'f' && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 24) {
			vk = (uint)(0x70 + (fn - 1)); // VK_F1 = 0x70
			return true;
		}

		if (key.Length == 1) {
			char upper = char.ToUpperInvariant(key[0]);
			if (upper is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) {
				vk = upper; // ASCII letters/digits double as their virtual-key codes.
				return true;
			}

			short scan = VkKeyScan(key[0]);
			if (scan != -1) {
				vk = (uint)(scan & 0xFF); // low byte is the VK; we ignore the shift state (modifiers come from the chord).
				return vk != 0;
			}
		}

		return false;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern short VkKeyScan(char ch);
}
