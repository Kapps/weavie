using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

internal static partial class X11 {
	private const string Lib = "libX11.so.6";

	internal const int KeyPress = 2;
	internal const int KeyRelease = 3;
	internal const int GrabModeAsync = 1;
	internal const uint ShiftMask = 1 << 0;
	internal const uint LockMask = 1 << 1;
	internal const uint ControlMask = 1 << 2;
	internal const uint Mod1Mask = 1 << 3;
	internal const uint Mod4Mask = 1 << 6;

	[StructLayout(LayoutKind.Sequential)]
	internal struct KeyEvent {
		internal int _type;
		internal nuint _serial;
		internal int _sendEvent;
		internal IntPtr _display;
		internal nuint _window;
		internal nuint _root;
		internal nuint _subwindow;
		internal nuint _time;
		internal int _x;
		internal int _y;
		internal int _rootX;
		internal int _rootY;
		internal uint _state;
		internal uint _keyCode;
		internal int _sameScreen;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ModifierKeymap {
		internal int _maxKeysPerModifier;
		internal IntPtr _modifierMap;
	}

	internal static uint FindModifierMask(IntPtr display, uint keyCode) {
		if (keyCode == 0) {
			return 0;
		}
		IntPtr mapPointer = XGetModifierMapping(display);
		if (mapPointer == IntPtr.Zero) {
			return 0;
		}

		try {
			var map = Marshal.PtrToStructure<ModifierKeymap>(mapPointer);
			for (int modifier = 0; modifier < 8; modifier++) {
				for (int key = 0; key < map._maxKeysPerModifier; key++) {
					int offset = (modifier * map._maxKeysPerModifier) + key;
					if (Marshal.ReadByte(map._modifierMap, offset) == keyCode) {
						return 1u << modifier;
					}
				}
			}

			return 0;
		} finally {
			XFreeModifiermap(mapPointer);
		}
	}

	[LibraryImport(Lib)]
	internal static partial int XDefaultScreen(IntPtr display);

	[LibraryImport(Lib)]
	internal static partial nuint XRootWindow(IntPtr display, int screenNumber);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nuint XStringToKeysym(string name);

	[LibraryImport(Lib)]
	internal static partial uint XKeysymToKeycode(IntPtr display, nuint keysym);

	[LibraryImport(Lib)]
	internal static partial void XGrabKey(
		IntPtr display,
		int keyCode,
		uint modifiers,
		nuint grabWindow,
		int ownerEvents,
		int pointerMode,
		int keyboardMode);

	[LibraryImport(Lib)]
	internal static partial void XUngrabKey(IntPtr display, int keyCode, uint modifiers, nuint grabWindow);

	[LibraryImport(Lib)]
	internal static partial int XSync(IntPtr display, int discard);

	[LibraryImport(Lib)]
	private static partial IntPtr XGetModifierMapping(IntPtr display);

	[LibraryImport(Lib)]
	private static partial int XFreeModifiermap(IntPtr modifierMap);
}
