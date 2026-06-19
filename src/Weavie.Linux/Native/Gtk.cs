using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// Minimal P/Invoke into GTK 3 — just enough to create the top-level window that hosts the WebKit
/// view, restore/capture its geometry, and run the main loop.
/// </summary>
internal static partial class Gtk {
	private const string Lib = "libgtk-3.so.0";

	/// <summary><c>GTK_WINDOW_TOPLEVEL</c>, the only window type the host creates.</summary>
	internal const int WindowToplevel = 0;

	[LibraryImport(Lib)]
	internal static partial void gtk_init(IntPtr argc, IntPtr argv);

	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_window_new(int type);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_window_set_title(IntPtr window, string title);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_set_default_size(IntPtr window, int width, int height);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_move(IntPtr window, int x, int y);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_maximize(IntPtr window);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_get_size(IntPtr window, out int width, out int height);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_get_position(IntPtr window, out int x, out int y);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gtk_window_is_maximized(IntPtr window);

	[LibraryImport(Lib)]
	internal static partial void gtk_container_add(IntPtr container, IntPtr widget);

	[LibraryImport(Lib)]
	internal static partial void gtk_widget_show_all(IntPtr widget);

	[LibraryImport(Lib)]
	internal static partial void gtk_main();

	[LibraryImport(Lib)]
	internal static partial void gtk_main_quit();
}
