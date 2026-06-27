using System.Runtime.InteropServices;
using System.Text.Json;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// The empty state: when launch resolves no workspace, the host loads welcome.html instead of the app and routes
// its Open Folder / Open Recent actions (the shared `menu-action` bridge messages) to the native folder picker and
// recents — opening a folder transitions this same window into the live workspace (OpenWorkspace).
internal sealed partial class WorkspaceHost {
	private void ShowWelcome() {
		// Recents reach the page as window.__WEAVIE_WELCOME__, injected before navigation (no flash, no round-trip).
		InjectAtDocumentStart($"window.__WEAVIE_WELCOME__ = {WelcomeConfigJson()};");
		_bridge.MessageReceived += OnWelcomeMessage;
		Gtk.gtk_window_set_default_size(_window, WelcomeWidth, WelcomeHeight);
		ShowWindow();
		WebKit.webkit_web_view_load_uri(_webView, "app://app/welcome.html");
	}

	// Routes the welcome screen's Open Folder / Open Recent to the picker / recents. Other messages no-op (the
	// welcome page sends nothing else); the core isn't wired yet, so there's no shell to forward to.
	private void OnWelcomeMessage(string json) {
		string action;
		string? path;
		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (!root.TryGetProperty("type", out var type) || type.GetString() != "menu-action") {
				return;
			}

			action = root.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
			path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
		} catch (JsonException) {
			return;
		}

		switch (action) {
			case "open-folder":
				if (PickFolder() is { } chosen) {
					OpenFromWelcome(chosen);
				}

				break;
			case "open-recent":
				if (string.IsNullOrEmpty(path)) {
					break;
				}

				// A folder gone since last launch: prune it and refresh the list so the dead row disappears.
				if (Directory.Exists(path)) {
					OpenFromWelcome(path);
				} else {
					_recents!.Remove(path);
					RefreshWelcomeRecents();
				}

				break;
		}
	}

	// Leaves the welcome surface for the live workspace in this same window; stops handling welcome messages first.
	private void OpenFromWelcome(string root) {
		_bridge.MessageReceived -= OnWelcomeMessage;
		OpenWorkspace(root);
	}

	// Re-injects the current recents and reloads welcome.html so a pruned entry drops out of the list.
	private void RefreshWelcomeRecents() {
		InjectAtDocumentStart($"window.__WEAVIE_WELCOME__ = {WelcomeConfigJson()};");
		WebKit.webkit_web_view_load_uri(_webView, "app://app/welcome.html");
	}

	private string WelcomeConfigJson() => JsonSerializer.Serialize(new { recents = _recents!.Items });

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
