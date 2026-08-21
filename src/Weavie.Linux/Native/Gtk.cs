using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// P/Invoke into GTK 4 — the top-level window that hosts the WebKit view, its geometry, the key controller,
/// and the native dialogs. GTK 4 ships GDK, GSK, and GTK in one library, so the GDK bindings share this name.
/// </summary>
internal static partial class Gtk {
	internal const string Lib = "libgtk-4.so.1";

	/// <summary><c>GTK_PHASE_CAPTURE</c> — run a controller before the widget it is attached to sees the event.</summary>
	internal const int PhaseCapture = 1;

	[LibraryImport(Lib)]
	internal static partial void gtk_init();

	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_window_new();

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_window_set_title(IntPtr window, string title);

	/// <summary>Names the themed icon the shell shows for the window — the one <c>LinuxDesktopIdentity</c> installs.</summary>
	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_window_set_icon_name(IntPtr window, string name);

	/// <summary>Hands the window the activation token that lets the compositor raise it.</summary>
	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_window_set_startup_id(IntPtr window, string startupId);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_set_default_size(IntPtr window, int width, int height);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_maximize(IntPtr window);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gtk_window_is_maximized(IntPtr window);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gtk_window_is_active(IntPtr window);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_set_child(IntPtr window, IntPtr child);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_present(IntPtr window);

	[LibraryImport(Lib)]
	internal static partial void gtk_window_destroy(IntPtr window);

	[LibraryImport(Lib)]
	internal static partial int gtk_widget_get_width(IntPtr widget);

	[LibraryImport(Lib)]
	internal static partial int gtk_widget_get_height(IntPtr widget);

	[LibraryImport(Lib)]
	internal static partial void gtk_widget_set_visible(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool visible);

	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_event_controller_key_new();

	[LibraryImport(Lib)]
	internal static partial void gtk_event_controller_set_propagation_phase(IntPtr controller, int phase);

	[LibraryImport(Lib)]
	internal static partial void gtk_widget_add_controller(IntPtr widget, IntPtr controller);

	/// <summary>Creates an alert with no formatted message; <see cref="gtk_alert_dialog_set_message"/> supplies the text.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_alert_dialog_new(IntPtr format);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_alert_dialog_set_message(IntPtr dialog, string message);

	[LibraryImport(Lib)]
	internal static partial void gtk_alert_dialog_set_modal(IntPtr dialog, [MarshalAs(UnmanagedType.Bool)] bool modal);

	[LibraryImport(Lib)]
	internal static partial void gtk_alert_dialog_choose(
		IntPtr dialog, IntPtr parent, IntPtr cancellable, IntPtr callback, IntPtr userData);

	[LibraryImport(Lib)]
	internal static partial int gtk_alert_dialog_choose_finish(IntPtr dialog, IntPtr result, out IntPtr error);

	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_file_dialog_new();

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void gtk_file_dialog_set_title(IntPtr dialog, string title);

	[LibraryImport(Lib)]
	internal static partial void gtk_file_dialog_select_folder(
		IntPtr dialog, IntPtr parent, IntPtr cancellable, IntPtr callback, IntPtr userData);

	/// <summary>The chosen folder as a GFile (unref when done), or NULL when the picker was cancelled.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr gtk_file_dialog_select_folder_finish(IntPtr dialog, IntPtr result, out IntPtr error);
}
