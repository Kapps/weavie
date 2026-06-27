using System.Text.Json;
using Foundation;
using Weavie.Core.Workspaces;

namespace Weavie.Mac;

// Workspace + chrome wiring: the menu's keybinding-chord lookup and File ▸ Open Folder / Open Recent, which open a
// new window (or focus the one already showing that folder).
public sealed partial class AppDelegate {
	/// <summary>The effective chord for a command id (first non-global resolved binding), or null if unbound.</summary>
	private string? ResolveChord(string commandId) =>
		_services?.Keybindings.Resolved.FirstOrDefault(binding => binding.Command == commandId && !binding.Global)?.Key;

	/// <summary>Shows the File ▸ Open Folder picker; the chosen folder opens in a new window via <see cref="OpenOrFocus"/>.</summary>
	private void OpenFolderInteractive() {
		var panel = NSOpenPanel.OpenPanel;
		panel.Title = "Open Folder";
		panel.CanChooseFiles = false;
		panel.CanChooseDirectories = true;
		panel.AllowsMultipleSelection = false;
		panel.CanCreateDirectories = true;
		if (panel.RunModal() == 1 && panel.Url is { Path: { } chosen }) {
			OpenOrFocus(chosen);
		}
	}

	/// <summary>
	/// User-driven open (File ▸ Open Folder / Open Recent): opens <paramref name="path"/> in a new window (or focuses
	/// the one already showing it) and persists it as the <c>workspace</c> to reopen on next launch.
	/// </summary>
	internal void OpenOrFocus(string path) {
		if (Open(path) is not null) {
			// Build the JSON string element by hand: JsonSerializer.Serialize is trim-unsafe (IL2026) on macOS.
			_services?.Settings.Set("workspace", JsonDocument.Parse("\"" + JsonEncodedText.Encode(path) + "\"").RootElement.Clone());
		}
	}

	/// <summary>
	/// Opens <paramref name="path"/> as a workspace window: focuses the existing window if already open, else creates
	/// one and records it in recents. Returns the window, or <c>null</c> if the folder no longer exists (a stale
	/// recents entry is pruned).
	/// </summary>
	private WorkspaceWindow? Open(string path) {
		if (string.IsNullOrEmpty(path)) {
			return null;
		}

		if (!Directory.Exists(path)) {
			_recents?.Remove(path);
			Frontmost?.Notify("error", $"Folder not found: {path}");
			return null;
		}

		_recents?.Add(path);
		// Dedupe on the same identity that keys the workspace's on-disk state (case-folded, fully-resolved), so two
		// paths reaching one folder focus the open window instead of opening a duplicate that clobbers its state.
		var id = WorkspaceId.ForPath(path);
		var existing = _windows.FirstOrDefault(w => w.Id == id);
		if (existing is not null) {
			Focus(existing);
			return existing;
		}

		var window = new WorkspaceWindow(this, path);
		_windows.Add(window);
		_lastActive = window;
		return window;
	}

	private void Focus(WorkspaceWindow window) {
		_lastActive = window;
		NSApplication.SharedApplication.Activate();
		window.Window.MakeKeyAndOrderFront(null);
	}
}
