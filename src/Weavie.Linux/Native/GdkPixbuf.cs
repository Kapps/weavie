using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>P/Invoke into gdk-pixbuf — load the app icon and encode clipboard images.</summary>
internal static partial class GdkPixbuf {
	private const string Lib = "libgdk_pixbuf-2.0.so.0";

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr gdk_pixbuf_new_from_file(string filename, IntPtr error);

	// gboolean gdk_pixbuf_save_to_bufferv(pixbuf, gchar **buffer, gsize *size, type, char **keys, char **vals,
	// GError **error). A NULL error (IntPtr.Zero) tells GLib to skip reporting, so there's nothing to free.
	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool gdk_pixbuf_save_to_bufferv(
		IntPtr pixbuf, out IntPtr buffer, out nuint size, string type, IntPtr optionKeys, IntPtr optionValues, IntPtr error);

	/// <summary>Loads a GdkPixbuf from <paramref name="path"/>, failing loudly when the asset is unavailable or invalid.</summary>
	internal static IntPtr LoadFile(string path) {
		IntPtr pixbuf = gdk_pixbuf_new_from_file(path, IntPtr.Zero);
		return pixbuf != IntPtr.Zero ? pixbuf : throw new InvalidOperationException($"Could not load app icon: {path}");
	}

	/// <summary>Encodes a GdkPixbuf to PNG bytes, or <c>null</c> when the encode fails.</summary>
	internal static byte[]? EncodePng(IntPtr pixbuf) {
		if (!gdk_pixbuf_save_to_bufferv(pixbuf, out IntPtr buffer, out nuint size, "png", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)) {
			return null;
		}

		try {
			byte[] bytes = new byte[size];
			Marshal.Copy(buffer, bytes, 0, (int)size);
			return bytes;
		} finally {
			GLib.g_free(buffer);
		}
	}
}
