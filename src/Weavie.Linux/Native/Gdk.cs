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
	internal const int FilterContinue = 0;
	internal const int FilterRemove = 2;

	internal enum DisplayBackend {
		X11,
		Wayland,
		Unknown,
	}

	internal static DisplayBackend GetDisplayBackend(IntPtr display) {
		string name = Marshal.PtrToStringUTF8(GLib.g_type_name_from_instance(display)) ?? string.Empty;
		return name switch {
			"GdkX11Display" => DisplayBackend.X11,
			"GdkWaylandDisplay" => DisplayBackend.Wayland,
			_ => DisplayBackend.Unknown,
		};
	}

	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_display_get_default();

	[LibraryImport(Lib)]
	internal static partial uint gdk_unicode_to_keyval(uint wc);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial uint gdk_keyval_from_name(string keyvalName);

	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_keyval_name(uint keyval);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gdk_wayland_display_set_startup_notification_id(IntPtr display, string startupId);

	[LibraryImport(Lib)]
	internal static partial void gdk_window_add_filter(IntPtr window, IntPtr function, IntPtr data);

	[LibraryImport(Lib)]
	internal static partial void gdk_window_remove_filter(IntPtr window, IntPtr function, IntPtr data);

	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_x11_display_get_xdisplay(IntPtr display);

	[LibraryImport(Lib)]
	internal static partial void gdk_x11_display_error_trap_push(IntPtr display);

	[LibraryImport(Lib)]
	internal static partial int gdk_x11_display_error_trap_pop(IntPtr display);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gdk_event_get_state(IntPtr keyEvent, out uint state);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gdk_event_get_keyval(IntPtr keyEvent, out uint keyval);
}
