using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>GDK event accessors and key/modifier values used by the GTK web-view keyboard bridge.</summary>
internal static partial class Gdk {
	private const string Lib = "libgdk-3.so.0";

	internal const uint ShiftMask = 1 << 0;
	internal const uint ControlMask = 1 << 2;
	internal const uint AltMask = 1 << 3;
	internal const uint SuperMask = 1 << 26;
	internal const uint HyperMask = 1 << 27;
	internal const uint MetaMask = 1 << 28;
	internal const uint Tab = 0xff09;
	internal const uint IsoLeftTab = 0xfe20;

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gdk_event_get_state(IntPtr keyEvent, out uint state);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gdk_event_get_keyval(IntPtr keyEvent, out uint keyval);
}
