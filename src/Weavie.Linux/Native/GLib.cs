using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// P/Invoke into GLib / GObject / GIO — signal connection, object lifetime, the main-loop idle queue (to
/// marshal work onto the GTK thread), and the in-memory input stream answering <c>app://</c> requests.
/// </summary>
internal static partial class GLib {
	private const string GObject = "libgobject-2.0.so.0";
	private const string GLibCore = "libglib-2.0.so.0";
	private const string Gio = "libgio-2.0.so.0";

	[LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial ulong g_signal_connect_data(
		IntPtr instance, string detailedSignal, IntPtr handler, IntPtr data, IntPtr destroyData, int connectFlags);

	[LibraryImport(GObject)]
	internal static partial void g_object_unref(IntPtr obj);

	[LibraryImport(GLibCore)]
	internal static partial void g_free(IntPtr mem);

	[LibraryImport(GLibCore)]
	internal static partial uint g_idle_add(IntPtr function, IntPtr data);

	[LibraryImport(Gio)]
	internal static partial IntPtr g_memory_input_stream_new_from_data(IntPtr data, nint len, IntPtr destroy);
}
