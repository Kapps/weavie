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

	/// <summary>An X protocol error, as the error handler receives it.</summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct ErrorEvent {
		internal int _type;
		internal IntPtr _display;
		internal nuint _resourceId;
		internal nuint _serial;
		internal byte _errorCode;
		internal byte _requestCode;
		internal byte _minorCode;
	}

	/// <summary>Opens a private connection to <paramref name="displayName"/> (NULL for <c>$DISPLAY</c>).</summary>
	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr XOpenDisplay(string? displayName);

	[LibraryImport(Lib)]
	internal static partial int XCloseDisplay(IntPtr display);

	/// <summary>The connection's file descriptor, so the main loop can watch it for readable events.</summary>
	[LibraryImport(Lib)]
	internal static partial int XConnectionNumber(IntPtr display);

	[LibraryImport(Lib)]
	internal static partial int XPending(IntPtr display);

	/// <summary>Reads the next event into a caller-owned buffer the size of an <c>XEvent</c> union.</summary>
	[LibraryImport(Lib)]
	internal static partial int XNextEvent(IntPtr display, IntPtr eventBuffer);

	/// <summary>Installs an error handler, returning the one it replaced. Xlib's default one exits the process.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr XSetErrorHandler(IntPtr handler);

	/// <summary>An <c>XEvent</c> is a union of 24 machine words; every read needs a buffer that large.</summary>
	internal static int EventSize => 24 * IntPtr.Size;
}
