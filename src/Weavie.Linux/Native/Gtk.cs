using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// P/Invoke into GTK 3 — create the top-level window that hosts the WebKit view, capture/restore its
/// geometry, and run the main loop.
/// </summary>
internal static partial class Gtk {
	private const string Lib = "libgtk-3.so.0";

	/// <summary><c>GTK_WINDOW_TOPLEVEL</c>, the only window type the host creates.</summary>
	internal const int WindowToplevel = 0;

	/// <summary><c>GDK_SELECTION_CLIPBOARD</c> as a GdkAtom (predefined atom 69) — the system clipboard selection.</summary>
	internal static readonly IntPtr SelectionClipboard = new(69);

	/// <summary><c>GTK_FILE_CHOOSER_ACTION_SELECT_FOLDER</c> — the Open Folder picker mode.</summary>
	internal const int FileChooserActionSelectFolder = 2;

	/// <summary><c>GTK_RESPONSE_ACCEPT</c> — the picker's accept response.</summary>
	internal const int ResponseAccept = -3;

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
	internal static partial void gtk_window_resize(IntPtr window, int width, int height);

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

	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_clipboard_get(IntPtr selection);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_clipboard_set_text(IntPtr clipboard, string text, int len);

	/// <summary>Persists the clipboard contents with the clipboard manager so they survive this process exiting.</summary>
	[LibraryImport(Lib)]
	internal static partial void gtk_clipboard_store(IntPtr clipboard);

	/// <summary>Returns a newly-allocated UTF-8 string (free with <see cref="GLib.g_free"/>) or NULL.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_clipboard_wait_for_text(IntPtr clipboard);

	/// <summary>Creates a native (OS-themed) file chooser; non-variadic, unlike <c>gtk_file_chooser_dialog_new</c>.</summary>
	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr gtk_file_chooser_native_new(
		string title, IntPtr parent, int action, string? acceptLabel, string? cancelLabel);

	/// <summary>Runs the native dialog modally, returning its response id (<see cref="ResponseAccept"/> on accept).</summary>
	[LibraryImport(Lib)]
	internal static partial int gtk_native_dialog_run(IntPtr dialog);

	/// <summary>The chosen path as a newly-allocated UTF-8 string (free with <see cref="GLib.g_free"/>), or NULL.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_file_chooser_get_filename(IntPtr chooser);

	[LibraryImport(Lib)]
	internal static partial void gtk_native_dialog_destroy(IntPtr dialog);
}
