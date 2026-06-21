using System.Diagnostics;
using System.Text.Json;
using Foundation;

namespace Weavie.Mac;

// Workspace + chrome wiring: the keybinding-chord lookup feeding the native menu, and File ▸ Open Folder /
// Open Recent (a workspace switch via app relaunch).
public sealed partial class AppDelegate {
	/// <summary>The effective chord for a command id (first non-global resolved binding), or null if unbound.</summary>
	private string? ResolveChord(string commandId) =>
		_services?.Keybindings.Resolved.FirstOrDefault(binding => binding.Command == commandId && !binding.Global)?.Key;

	/// <summary>
	/// Shows the native folder picker (File ▸ Open Folder); the chosen folder becomes the workspace via
	/// <see cref="SwitchWorkspace"/>.
	/// </summary>
	private void OpenFolderInteractive() {
		var panel = NSOpenPanel.OpenPanel;
		panel.Title = "Open Folder";
		panel.CanChooseFiles = false;
		panel.CanChooseDirectories = true;
		panel.AllowsMultipleSelection = false;
		panel.CanCreateDirectories = true;
		if (panel.RunModal() == 1 && panel.Url is { Path: { } chosen }) {
			SwitchWorkspace(chosen);
		}
	}

	/// <summary>
	/// Switches the workspace to <paramref name="path"/>: records it in recents, persists the <c>workspace</c>
	/// setting, and relaunches the app. A process hosts one workspace, so a switch is a clean relaunch.
	/// </summary>
	private void SwitchWorkspace(string path) {
		if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) {
			_core?.Notify("error", $"Folder not found: {path}");
			return;
		}

		if (Path.TrimEndingDirectorySeparator(path) == Path.TrimEndingDirectorySeparator(_workspace ?? string.Empty)) {
			return; // already this workspace
		}

		_recents?.Add(path);
		// Build the JSON string element by hand: JsonSerializer.Serialize is trim-unsafe (IL2026) on macOS.
		_services?.Settings.Set("workspace", JsonDocument.Parse("\"" + JsonEncodedText.Encode(path) + "\"").RootElement.Clone());
		RelaunchApp();
	}

	/// <summary>Launches a fresh instance of the app bundle and quits this one.</summary>
	private static void RelaunchApp() {
		string bundlePath = NSBundle.MainBundle.BundlePath;
		try {
			var startInfo = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
			startInfo.ArgumentList.Add("-n"); // open a new instance even though one is running
			startInfo.ArgumentList.Add(bundlePath);
			Process.Start(startInfo);
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			Console.Error.WriteLine($"[weavie] relaunch failed: {ex.Message}");
		}

		NSApplication.SharedApplication.Terminate(null);
	}
}
