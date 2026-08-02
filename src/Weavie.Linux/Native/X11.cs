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

	[LibraryImport(Lib)]
	internal static partial int XDefaultScreen(IntPtr display);

	[LibraryImport(Lib)]
	internal static partial nuint XRootWindow(IntPtr display, int screenNumber);

	[LibraryImport(Lib)]
	internal static partial uint XKeysymToKeycode(IntPtr display, nuint keysym);

	[LibraryImport(Lib)]
	internal static partial uint XkbKeysymToModifiers(IntPtr display, nuint keysym);

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

}
