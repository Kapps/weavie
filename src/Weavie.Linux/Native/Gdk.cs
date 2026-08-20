using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>GDK 4 — display/backend identity, keyval helpers, and the clipboard the host bus reads and writes.</summary>
internal static partial class Gdk {
	private const string Lib = Gtk.Lib;

	internal const uint ShiftMask = 1 << 0;
	internal const uint ControlMask = 1 << 2;
	internal const uint AltMask = 1 << 3;
	internal const uint SuperMask = 1 << 26;
	internal const uint HyperMask = 1 << 27;
	internal const uint MetaMask = 1 << 28;
	internal const uint Tab = 0xff09;
	internal const uint IsoLeftTab = 0xfe20;

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
	internal static partial IntPtr gdk_display_get_monitors(IntPtr display);

	[LibraryImport(Lib)]
	internal static partial int gdk_monitor_get_width_mm(IntPtr monitor);

	[LibraryImport(Lib)]
	internal static partial int gdk_monitor_get_height_mm(IntPtr monitor);

	/// <summary>The monitor's refresh rate in millihertz, or 0 when the compositor does not report one.</summary>
	[LibraryImport(Lib)]
	internal static partial int gdk_monitor_get_refresh_rate(IntPtr monitor);

	[LibraryImport(Lib)]
	internal static partial uint gdk_unicode_to_keyval(uint wc);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial uint gdk_keyval_from_name(string keyvalName);

	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_keyval_name(uint keyval);

	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_display_get_clipboard(IntPtr display);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gdk_clipboard_set_text(IntPtr clipboard, string text);

	[LibraryImport(Lib)]
	internal static partial void gdk_clipboard_read_text_async(
		IntPtr clipboard, IntPtr cancellable, IntPtr callback, IntPtr userData);

	/// <summary>The clipboard text as a newly-allocated UTF-8 string (free with <see cref="GLib.g_free"/>), or NULL.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_clipboard_read_text_finish(IntPtr clipboard, IntPtr result, out IntPtr error);

	[LibraryImport(Lib)]
	internal static partial void gdk_clipboard_read_texture_async(
		IntPtr clipboard, IntPtr cancellable, IntPtr callback, IntPtr userData);

	/// <summary>The clipboard image as a GdkTexture (unref when done), or NULL.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_clipboard_read_texture_finish(IntPtr clipboard, IntPtr result, out IntPtr error);

	/// <summary>Encodes a GdkTexture as PNG into a GBytes (unref with <see cref="GLib.g_bytes_unref"/>).</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gdk_texture_save_to_png_bytes(IntPtr texture);
}
