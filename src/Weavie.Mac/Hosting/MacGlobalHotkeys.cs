using System.Runtime.InteropServices;
using CoreFoundation;
using Weavie.Core.Commands;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The macOS <see cref="IGlobalHotkeyRegistrar"/>: registers system-wide hotkeys via Carbon's
/// <c>RegisterEventHotKey</c> (no Accessibility/Input-Monitoring permission needed; fires while unfocused),
/// routing presses through one app-level Carbon handler. One per process; all Carbon calls + the callback run
/// on the main thread (<see cref="Apply"/> marshals there via the main dispatch queue).
/// </summary>
internal sealed class MacGlobalHotkeys : IGlobalHotkeyRegistrar {
	private const string CarbonFramework = "/System/Library/Frameworks/Carbon.framework/Carbon";
	private const uint EventClassKeyboard = 0x6B657962; // 'keyb'
	private const uint EventHotKeyPressed = 6;           // kEventHotKeyPressed
	private const uint TypeEventHotKeyID = 0x686B6964;   // 'hkid'
	private const uint ParamDirectObject = 0x2D2D2D2D;   // '----' (kEventParamDirectObject)
	private const uint HotKeySignature = 0x77766965;     // 'wvie' — our hotkey id namespace

	[Flags]
	private enum CarbonModifiers : uint {
		Cmd = 0x0100,
		Shift = 0x0200,
		Option = 0x0800,
		Control = 0x1000,
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EventTypeSpec {
		public uint EventClass;
		public uint EventKind;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EventHotKeyId {
		public uint Signature;
		public uint Id;
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int EventHandlerProc(IntPtr callRef, IntPtr theEvent, IntPtr userData);

	// Carbon virtual key codes (kVK_*, HIToolbox/Events.h) for named keys + punctuation a chord can name.
	// Letters/digits and function keys have their own tables below.
	private static readonly Dictionary<string, uint> KeyCodes = new(StringComparer.Ordinal) {
		["space"] = 49,
		["enter"] = 36,
		["return"] = 36,
		["tab"] = 48,
		["escape"] = 53,
		["esc"] = 53,
		["backspace"] = 51,
		["delete"] = 117,
		["del"] = 117,
		["insert"] = 114,
		["home"] = 115,
		["end"] = 119,
		["pageup"] = 116,
		["pagedown"] = 121,
		["up"] = 126,
		["down"] = 125,
		["left"] = 123,
		["right"] = 124,
		["`"] = 50,
		["-"] = 27,
		["="] = 24,
		["["] = 33,
		["]"] = 30,
		["\\"] = 42,
		[";"] = 41,
		["'"] = 39,
		[","] = 43,
		["."] = 47,
		["/"] = 44,
	};

	// kVK_ANSI_A..Z indexed by letter; codes follow the physical key layout, not alphabetical order.
	private static readonly Dictionary<char, uint> Letters = new() {
		['a'] = 0,
		['b'] = 11,
		['c'] = 8,
		['d'] = 2,
		['e'] = 14,
		['f'] = 3,
		['g'] = 5,
		['h'] = 4,
		['i'] = 34,
		['j'] = 38,
		['k'] = 40,
		['l'] = 37,
		['m'] = 46,
		['n'] = 45,
		['o'] = 31,
		['p'] = 35,
		['q'] = 12,
		['r'] = 15,
		['s'] = 1,
		['t'] = 17,
		['u'] = 32,
		['v'] = 9,
		['w'] = 13,
		['x'] = 7,
		['y'] = 16,
		['z'] = 6,
	};

	private static readonly Dictionary<char, uint> Digits = new() {
		['0'] = 29,
		['1'] = 18,
		['2'] = 19,
		['3'] = 20,
		['4'] = 21,
		['5'] = 23,
		['6'] = 22,
		['7'] = 26,
		['8'] = 28,
		['9'] = 25,
	};

	// F1..F20 key codes (non-sequential).
	private static readonly uint[] FunctionKeys =
		[122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90];

	private readonly EventHandlerProc _handler; // kept alive so the native callback isn't GC'd
	private readonly object _gate = new();
	private readonly Dictionary<uint, (IntPtr Ref, GlobalHotkey Hotkey)> _registered = [];
	private IReadOnlyList<GlobalHotkey> _desired = [];
	private IntPtr _handlerRef;
	private bool _installAttempted;
	private uint _nextId = 1;
	private bool _disposed;

	/// <summary>Creates the registrar; the Carbon event handler is installed lazily on first <see cref="Apply"/>.</summary>
	public MacGlobalHotkeys() {
		_handler = OnHotKeyEvent;
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

		DispatchQueue.MainQueue.DispatchAsync(ApplyOnMain);
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
		}

		DispatchQueue.MainQueue.DispatchAsync(() => {
			UnregisterAll();
			if (_handlerRef != IntPtr.Zero) {
				RemoveEventHandler(_handlerRef);
				_handlerRef = IntPtr.Zero;
			}
		});
	}

	private void ApplyOnMain() {
		IReadOnlyList<GlobalHotkey> desired;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			desired = _desired;
		}

		if (!EnsureHandlerInstalled()) {
			return;
		}

		UnregisterAll();
		foreach (var hotkey in desired) {
			if (!TryMap(hotkey, out uint code, out uint modifiers)) {
				Log?.Invoke($"[hotkey] can't map '{hotkey.Chord}' to a macOS key; skipping global binding for '{hotkey.Command}'.");
				continue;
			}

			uint id = _nextId++;
			var hkid = new EventHotKeyId { Signature = HotKeySignature, Id = id };
			int status = RegisterEventHotKey(code, modifiers, hkid, GetApplicationEventTarget(), 0, out IntPtr href);
			if (status == 0 && href != IntPtr.Zero) {
				lock (_gate) {
					_registered[id] = (href, hotkey);
				}
			} else {
				Log?.Invoke($"[hotkey] RegisterEventHotKey failed for '{hotkey.Chord}' (OSStatus {status}); another app may own it.");
			}
		}
	}

	// Install the single application keyboard-hotkey handler once; returns whether it's installed.
	private bool EnsureHandlerInstalled() {
		if (_handlerRef != IntPtr.Zero) {
			return true;
		}

		if (_installAttempted) {
			return false; // already failed once; don't spam the log on every Apply
		}

		_installAttempted = true;
		EventTypeSpec[] spec = [new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed }];
		int status = InstallEventHandler(GetApplicationEventTarget(), _handler, 1, spec, IntPtr.Zero, out _handlerRef);
		if (status != 0) {
			Log?.Invoke($"[hotkey] InstallEventHandler failed (OSStatus {status}); global hotkeys are unavailable.");
			_handlerRef = IntPtr.Zero;
			return false;
		}

		return true;
	}

	private void UnregisterAll() {
		lock (_gate) {
			foreach (var (hotkeyRef, _) in _registered.Values) {
				UnregisterHotKey(hotkeyRef);
			}

			_registered.Clear();
		}
	}

	private int OnHotKeyEvent(IntPtr callRef, IntPtr theEvent, IntPtr userData) {
		int status = GetEventParameter(
			theEvent, ParamDirectObject, TypeEventHotKeyID, IntPtr.Zero,
			(nuint)Marshal.SizeOf<EventHotKeyId>(), IntPtr.Zero, out var hkid);
		if (status != 0) {
			return 0; // noErr — let the event continue
		}

		GlobalHotkey? hotkey = null;
		lock (_gate) {
			if (_registered.TryGetValue(hkid.Id, out var entry)) {
				hotkey = entry.Hotkey;
			}
		}

		if (hotkey is not null) {
			Pressed?.Invoke(hotkey);
		}

		return 0;
	}

	private static bool TryMap(GlobalHotkey hotkey, out uint code, out uint modifiers) {
		var mods = (CarbonModifiers)0;
		var m = hotkey.Modifiers;
		// $mod and Meta both resolve to Cmd on macOS.
		if (m.HasFlag(HotkeyModifiers.Mod) || m.HasFlag(HotkeyModifiers.Meta)) {
			mods |= CarbonModifiers.Cmd;
		}

		if (m.HasFlag(HotkeyModifiers.Ctrl)) {
			mods |= CarbonModifiers.Control;
		}

		if (m.HasFlag(HotkeyModifiers.Shift)) {
			mods |= CarbonModifiers.Shift;
		}

		if (m.HasFlag(HotkeyModifiers.Alt)) {
			mods |= CarbonModifiers.Option;
		}

		modifiers = (uint)mods;
		return TryMapKey(hotkey.Key, out code);
	}

	private static bool TryMapKey(string key, out uint code) {
		code = 0;
		if (string.IsNullOrEmpty(key)) {
			return false;
		}

		if (KeyCodes.TryGetValue(key, out code)) {
			return true;
		}

		if (key.Length >= 2 && key[0] == 'f' && int.TryParse(key.AsSpan(1), out int fn) && fn >= 1 && fn <= FunctionKeys.Length) {
			code = FunctionKeys[fn - 1];
			return true;
		}

		if (key.Length == 1) {
			if (Letters.TryGetValue(char.ToLowerInvariant(key[0]), out code)) {
				return true;
			}

			if (Digits.TryGetValue(key[0], out code)) {
				return true;
			}
		}

		return false;
	}

	[DllImport(CarbonFramework)]
	private static extern IntPtr GetApplicationEventTarget();

	[DllImport(CarbonFramework)]
	private static extern int InstallEventHandler(
		IntPtr inTarget, EventHandlerProc inHandler, nuint inNumTypes, [In] EventTypeSpec[] inList,
		IntPtr inUserData, out IntPtr outRef);

	[DllImport(CarbonFramework)]
	private static extern int RemoveEventHandler(IntPtr inHandlerRef);

	[DllImport(CarbonFramework)]
	private static extern int RegisterEventHotKey(
		uint inHotKeyCode, uint inHotKeyModifiers, EventHotKeyId inHotKeyId, IntPtr inTarget, uint inOptions,
		out IntPtr outRef);

	[DllImport(CarbonFramework)]
	private static extern int UnregisterHotKey(IntPtr inHotKey);

	[DllImport(CarbonFramework)]
	private static extern int GetEventParameter(
		IntPtr inEvent, uint inName, uint inDesiredType, IntPtr outActualType, nuint inBufferSize,
		IntPtr outActualSize, out EventHotKeyId outData);
}
