using System.Runtime.InteropServices;
using Weavie.Core.Theming;
using Weavie.Hosting.Web;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// The empty state: when launch resolves no workspace, the shared WelcomeController loads welcome.html and routes its
// Open Folder / Open Recent to the native folder picker / recents. Opening a folder transitions this same window
// into the live workspace (OpenWorkspace). The protocol + recents JSON live in Weavie.Hosting.Web.WelcomeController.
internal sealed partial class WorkspaceHost {
	private WelcomeController? _welcome;

	private void ShowWelcome() {
		_welcome = new WelcomeController(
			_bridge,
			this,
			"app://app/welcome.html",
			() => _recents!.Items,
			() => ThemeJson.Build(_services!.Settings, _services.ThemeOverrides, Log),
			OpenFolder,
			OpenRecent);
		Gtk.gtk_window_set_default_size(_window, WelcomeWidth, WelcomeHeight);
		ShowWindow();
		_ = _welcome.ShowAsync();
	}

	private void OpenFolder() {
		if (PickFolder() is { } chosen) {
			OpenFromWelcome(chosen);
		}
	}

	private void OpenRecent(string path) {
		// A folder gone since last launch: prune it and refresh the list so the dead row disappears.
		if (Directory.Exists(path)) {
			OpenFromWelcome(path);
		} else {
			_recents!.Remove(path);
			_ = _welcome!.RefreshAsync();
		}
	}

	// Leaves the welcome surface for the live workspace in this same window; stops routing welcome messages first.
	private void OpenFromWelcome(string root) {
		_welcome!.Detach();
		_welcome = null;
		OpenWorkspace(root);
	}

	// The native (OS-themed) Open Folder picker; returns the chosen directory or null if cancelled.
	private string? PickFolder() {
		IntPtr dialog = Gtk.gtk_file_dialog_new();
		Gtk.gtk_file_dialog_set_title(dialog, "Open Folder");
		try {
			return MainLoopWait.For(
				callback => Gtk.gtk_file_dialog_select_folder(dialog, _window, IntPtr.Zero, callback, IntPtr.Zero),
				result => ChosenPath(dialog, result));
		} finally {
			GLib.g_object_unref(dialog);
		}
	}

	// Cancelling the picker is reported as an error, so a null folder and a reported error both mean "no choice".
	private static string? ChosenPath(IntPtr dialog, IntPtr result) {
		IntPtr folder = Gtk.gtk_file_dialog_select_folder_finish(dialog, result, out IntPtr error);
		GLib.g_clear_error(ref error);
		if (folder == IntPtr.Zero) {
			return null;
		}

		try {
			IntPtr path = GLib.g_file_get_path(folder);
			if (path == IntPtr.Zero) {
				return null;
			}

			try {
				return Marshal.PtrToStringUTF8(path);
			} finally {
				GLib.g_free(path);
			}
		} finally {
			GLib.g_object_unref(folder);
		}
	}
}
