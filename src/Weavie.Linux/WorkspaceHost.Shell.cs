using Weavie.Core.Shell;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// Native application/workspace actions for the web-rendered Linux File menu. Window-manager controls remain
// GTK-owned and are intentionally absent from this adapter.
internal sealed partial class WorkspaceHost {
	void IShellMenuActions.ShowOpenFolderPicker() {
		if (PickFolder() is { } chosen) {
			ReplaceWorkspace(chosen);
		}
	}

	void IShellMenuActions.OpenWorkspace(string path) {
		if (Directory.Exists(path)) {
			ReplaceWorkspace(path);
		} else {
			_recents!.Remove(path);
		}
	}

	void IShellMenuActions.CloseWindow() {
		CloseWorkspace();
		WebKit.webkit_user_content_manager_remove_all_scripts(_contentManager);
		ShowWelcome();
	}

	void IShellMenuActions.Quit() => Gtk.gtk_widget_destroy(_window);

	private void ReplaceWorkspace(string root) {
		_welcome?.Detach();
		_welcome = null;
		CloseWorkspace();
		OpenWorkspace(root);
	}

	private void CloseWorkspace() {
		if (_core is null) {
			return;
		}

		SaveWindowState();
		_core.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_core = null;
	}
}
