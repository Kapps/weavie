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

	[LibraryImport(GObject)]
	internal static partial IntPtr g_type_name_from_instance(IntPtr instance);

	[LibraryImport(GLibCore, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void g_set_prgname(string prgname);

	[LibraryImport(GLibCore)]
	internal static partial void g_free(IntPtr mem);

	/// <summary>Frees a GError an async <c>_finish</c> reported (a cancelled picker reports one) and clears it.</summary>
	[LibraryImport(GLibCore)]
	internal static partial void g_clear_error(ref IntPtr error);

	[LibraryImport(GLibCore)]
	internal static partial uint g_idle_add(IntPtr function, IntPtr data);

	[LibraryImport(GLibCore)]
	internal static partial IntPtr g_main_loop_new(IntPtr context, [MarshalAs(UnmanagedType.Bool)] bool isRunning);

	[LibraryImport(GLibCore)]
	internal static partial void g_main_loop_run(IntPtr loop);

	[LibraryImport(GLibCore)]
	internal static partial void g_main_loop_quit(IntPtr loop);

	[LibraryImport(GLibCore)]
	internal static partial void g_main_loop_unref(IntPtr loop);

	/// <summary><c>G_PRIORITY_DEFAULT</c>.</summary>
	internal const int PriorityDefault = 0;

	/// <summary><c>G_IO_IN</c> — the descriptor has data to read.</summary>
	internal const int IoIn = 1;

	/// <summary>Watches a file descriptor on the main loop; returns the source id (remove with <see cref="g_source_remove"/>).</summary>
	[LibraryImport(GLibCore)]
	internal static partial uint g_unix_fd_add_full(
		int priority, int fd, int condition, IntPtr function, IntPtr data, IntPtr notify);

	[LibraryImport(GLibCore)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool g_source_remove(uint sourceId);

	/// <summary>Borrowed pointer to a GBytes' data; valid until the GBytes is unreffed.</summary>
	[LibraryImport(GLibCore)]
	internal static partial IntPtr g_bytes_get_data(IntPtr bytes, out nuint size);

	[LibraryImport(GLibCore)]
	internal static partial void g_bytes_unref(IntPtr bytes);

	[LibraryImport(Gio)]
	internal static partial IntPtr g_memory_input_stream_new_from_data(IntPtr data, nint len, IntPtr destroy);

	/// <summary>A GFile's local path as a newly-allocated string (free with <see cref="g_free"/>), or NULL.</summary>
	[LibraryImport(Gio)]
	internal static partial IntPtr g_file_get_path(IntPtr file);

	[LibraryImport(Gio)]
	internal static partial uint g_list_model_get_n_items(IntPtr model);

	/// <summary>The item at <paramref name="position"/>, owning a reference the caller must release.</summary>
	[LibraryImport(Gio)]
	internal static partial IntPtr g_list_model_get_item(IntPtr model, uint position);
}
