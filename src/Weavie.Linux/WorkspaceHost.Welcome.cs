using System.Runtime.InteropServices;
using Weavie.Hosting.Web;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// The empty state: when launch resolves no workspace, the shared WelcomeController loads welcome.html and routes its
// Open Folder / Open Recent to the native folder picker / recents. Opening a folder transitions this same window
// into the live workspace (OpenWorkspace). The protocol + recents JSON live in Weavie.Hosting.Web.WelcomeController.
internal sealed partial class WorkspaceHost {
	private WelcomeController? _welcome;

	private void ShowWelcome() {
		_welcome = new WelcomeController(_bridge, this, "app://app/welcome.html", () => _recents!.Items, OpenFolder, OpenRecent);
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
		OpenWorkspace(root);
	}

	// The native (OS-themed) Open Folder picker; returns the chosen directory or null if cancelled.
	private string? PickFolder() {
		IntPtr dialog = Gtk.gtk_file_chooser_native_new(
			"Open Folder", _window, Gtk.FileChooserActionSelectFolder, "_Open", "_Cancel");
		try {
			if (Gtk.gtk_native_dialog_run(dialog) != Gtk.ResponseAccept) {
				return null;
			}

			IntPtr namePtr = Gtk.gtk_file_chooser_get_filename(dialog);
			if (namePtr == IntPtr.Zero) {
				return null;
			}

			try {
				return Marshal.PtrToStringUTF8(namePtr);
			} finally {
				GLib.g_free(namePtr);
			}
		} finally {
			Gtk.gtk_native_dialog_destroy(dialog);
		}
	}
}
